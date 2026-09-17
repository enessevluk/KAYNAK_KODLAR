using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;

sealed class RavenDb
{
    private readonly string connectionString;
    public RavenDb(string path) => connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString); c.Open();
        using var pragma = c.CreateCommand(); pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;"; pragma.ExecuteNonQuery();
        return c;
    }

    public void Initialize()
    {
        using var c = Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
CREATE TABLE IF NOT EXISTS licenses(
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 key_hash TEXT NOT NULL UNIQUE,
 key_last4 TEXT NOT NULL,
 status TEXT NOT NULL DEFAULT 'ACTIVE',
 status_reason TEXT NOT NULL DEFAULT '',
 steam_id TEXT NULL,
 device_hash TEXT NULL,
 activation_token_hash TEXT NULL,
 expires_utc TEXT NULL,
 created_utc TEXT NOT NULL,
 first_activation_utc TEXT NULL,
 last_seen_utc TEXT NULL,
 last_client_version TEXT NOT NULL DEFAULT '',
 note TEXT NOT NULL DEFAULT '',
 source TEXT NOT NULL DEFAULT 'LEGACY',
 shopier_order_id TEXT NULL,
 shopier_product_id TEXT NULL,
 purchase_utc TEXT NULL,
 plan TEXT NOT NULL DEFAULT 'STANDARD',
 next_plan TEXT NULL,
 plan_changes_utc TEXT NULL
);
CREATE TABLE IF NOT EXISTS auth_sessions(
 session_id TEXT PRIMARY KEY,
 steam_id TEXT NULL,
 expires_utc TEXT NOT NULL,
 used_utc TEXT NULL
);
CREATE TABLE IF NOT EXISTS releases(
 version TEXT PRIMARY KEY,
 mandatory INTEGER NOT NULL,
 minimum_version TEXT NOT NULL,
 file_name TEXT NOT NULL,
 sha256 TEXT NOT NULL,
 signature TEXT NOT NULL,
 notes TEXT NOT NULL,
 created_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS server_meta(
 key TEXT PRIMARY KEY,
 value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS schema_migrations(
 version INTEGER PRIMARY KEY,
 name TEXT NOT NULL,
 applied_utc TEXT NOT NULL
);
INSERT OR IGNORE INTO server_meta(key,value) VALUES('instance_id', lower(hex(randomblob(16))));
CREATE TABLE IF NOT EXISTS shopier_orders(
 order_id TEXT PRIMARY KEY,
 steam_id TEXT NOT NULL DEFAULT '',
 product_id TEXT NOT NULL DEFAULT '',
 product_title TEXT NOT NULL DEFAULT '',
 payment_status TEXT NOT NULL DEFAULT '',
 order_note TEXT NOT NULL DEFAULT '',
 status TEXT NOT NULL,
 error TEXT NOT NULL DEFAULT '',
 license_id INTEGER NULL,
 received_utc TEXT NOT NULL,
 FOREIGN KEY(license_id) REFERENCES licenses(id)
);
CREATE TABLE IF NOT EXISTS admin_audit(
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 created_utc TEXT NOT NULL,
 actor TEXT NOT NULL,
 action TEXT NOT NULL,
 target TEXT NOT NULL DEFAULT '',
 detail TEXT NOT NULL DEFAULT '',
 remote_ip TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_license_token ON licenses(activation_token_hash);
CREATE INDEX IF NOT EXISTS idx_license_steam ON licenses(steam_id);
CREATE INDEX IF NOT EXISTS idx_shopier_steam ON shopier_orders(steam_id);
CREATE INDEX IF NOT EXISTS idx_admin_audit_created ON admin_audit(created_utc DESC);
""";
            cmd.ExecuteNonQuery();
        }
        ApplyMigration(c, 1, "initial licensing schema", () => { });
        ApplyMigration(c, 2, "shopier commerce columns", () =>
        {
            EnsureColumn(c, "licenses", "source", "TEXT NOT NULL DEFAULT 'LEGACY'");
            EnsureColumn(c, "licenses", "shopier_order_id", "TEXT NULL");
            EnsureColumn(c, "licenses", "shopier_product_id", "TEXT NULL");
            EnsureColumn(c, "licenses", "purchase_utc", "TEXT NULL");
        });
        ApplyMigration(c, 3, "standard premium entitlement", () =>
            EnsureColumn(c, "licenses", "plan", "TEXT NOT NULL DEFAULT 'STANDARD'"));
        ApplyMigration(c, 4, "admin audit trail", () => { });
        ApplyMigration(c, 5, "scheduled plan transitions", () =>
        {
            EnsureColumn(c, "licenses", "next_plan", "TEXT NULL");
            EnsureColumn(c, "licenses", "plan_changes_utc", "TEXT NULL");
        });
        ApplyMigration(c, 6, "license status reason", () =>
            EnsureColumn(c, "licenses", "status_reason", "TEXT NOT NULL DEFAULT ''"));
        EnsureMeta(c, "shopier_baseline_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        EnsureMeta(c, "shopier_cursor_utc", GetMeta(c, "shopier_baseline_utc") ?? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        EnsureMeta(c, "shopier_initial_sync_completed", "0");
        EnsureMeta(c, "maintenance_enabled", "0");
        EnsureMeta(c, "maintenance_message", "");
        EnsureMeta(c, "announcement_id", "");
        EnsureMeta(c, "announcement_title", "");
        EnsureMeta(c, "announcement_message", "");
        EnsureMeta(c, "announcement_level", "INFO");
        EnsureMeta(c, "announcement_image_url", "");
        EnsureMeta(c, "announcement_button_text", "");
        EnsureMeta(c, "announcement_button_url", "");
        EnsureMeta(c, "announcement_starts_utc", "");
        EnsureMeta(c, "announcement_ends_utc", "");
        EnsureMeta(c, "home_media_urls", "");
    }

    public string GetServerInstanceId()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM server_meta WHERE key='instance_id' LIMIT 1";
        var value = cmd.ExecuteScalar()?.ToString();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Raven server instance kimligi olusturulamadi.");
        return value;
    }

    public DateTimeOffset GetOrCreateShopierBaselineUtc()
    {
        using var c = Open();
        var text = GetMeta(c, "shopier_baseline_utc");
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return parsed;
        var now = DateTimeOffset.UtcNow;
        SetMeta(c, "shopier_baseline_utc", now.ToString("O", CultureInfo.InvariantCulture));
        return now;
    }

    public DateTimeOffset GetShopierCursorUtc()
    {
        using var c = Open();
        var text = GetMeta(c, "shopier_cursor_utc");
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return parsed;

        var baselineText = GetMeta(c, "shopier_baseline_utc");
        var baseline = DateTimeOffset.TryParse(baselineText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var baselineParsed)
            ? baselineParsed
            : DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(baselineText))
            SetMeta(c, "shopier_baseline_utc", baseline.ToString("O", CultureInfo.InvariantCulture));
        SetMeta(c, "shopier_cursor_utc", baseline.ToString("O", CultureInfo.InvariantCulture));
        return baseline;
    }

    public void SetShopierCursorUtc(DateTimeOffset value)
    {
        using var c = Open();
        SetMeta(c, "shopier_cursor_utc", value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    public void RewindShopierCursorUtc(DateTimeOffset value)
    {
        using var c = Open();
        var requested = value.ToUniversalTime();
        var text = GetMeta(c, "shopier_cursor_utc");
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var current) || requested < current)
            SetMeta(c, "shopier_cursor_utc", requested.ToString("O", CultureInfo.InvariantCulture));
    }

    public bool IsShopierInitialSyncCompleted()
    {
        using var c = Open();
        return string.Equals(GetMeta(c, "shopier_initial_sync_completed"), "1", StringComparison.Ordinal);
    }

    public void MarkShopierInitialSyncCompleted()
    {
        using var c = Open();
        SetMeta(c, "shopier_initial_sync_completed", "1");
    }

    public OperationalSettings GetOperationalSettings()
    {
        using var c=Open();
        return new(
            string.Equals(GetMeta(c,"maintenance_enabled"),"1",StringComparison.Ordinal),
            GetMeta(c,"maintenance_message")??"",
            GetMeta(c,"announcement_id")??"",
            GetMeta(c,"announcement_title")??"",
            GetMeta(c,"announcement_message")??"",
            NormalizeAnnouncementLevel(GetMeta(c,"announcement_level")),
            GetMeta(c,"announcement_image_url")??"",
            GetMeta(c,"announcement_button_text")??"",
            GetMeta(c,"announcement_button_url")??"",
            ParseUtc(GetMeta(c,"announcement_starts_utc")),
            ParseUtc(GetMeta(c,"announcement_ends_utc")),
            GetMeta(c,"home_media_urls")??"");
    }

    public void SetOperationalSettings(bool maintenanceEnabled, string maintenanceMessage, string announcementTitle, string announcementMessage,
        string announcementLevel="INFO",string announcementImageUrl="",string announcementButtonText="",string announcementButtonUrl="",
        DateTimeOffset? announcementStartsUtc=null,DateTimeOffset? announcementEndsUtc=null,string? homeMediaUrls=null)
    {
        using var c=Open();
        var oldTitle=GetMeta(c,"announcement_title")??""; var oldMessage=GetMeta(c,"announcement_message")??"";
        var oldRevision=string.Join('|',oldTitle,oldMessage,GetMeta(c,"announcement_level"),GetMeta(c,"announcement_image_url"),
            GetMeta(c,"announcement_button_text"),GetMeta(c,"announcement_button_url"),GetMeta(c,"announcement_starts_utc"),GetMeta(c,"announcement_ends_utc"));
        var title=(announcementTitle??"").Trim(); var message=(announcementMessage??"").Trim();
        SetMeta(c,"maintenance_enabled",maintenanceEnabled?"1":"0");
        SetMeta(c,"maintenance_message",(maintenanceMessage??"").Trim());
        SetMeta(c,"announcement_title",title); SetMeta(c,"announcement_message",message);
        SetMeta(c,"announcement_level",NormalizeAnnouncementLevel(announcementLevel));
        SetMeta(c,"announcement_image_url",(announcementImageUrl??"").Trim());
        SetMeta(c,"announcement_button_text",(announcementButtonText??"").Trim());
        SetMeta(c,"announcement_button_url",(announcementButtonUrl??"").Trim());
        SetMeta(c,"announcement_starts_utc",announcementStartsUtc?.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture)??"");
        SetMeta(c,"announcement_ends_utc",announcementEndsUtc?.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture)??"");
        if (homeMediaUrls is not null)
            SetMeta(c,"home_media_urls",homeMediaUrls.Trim());
        var newRevision=string.Join('|',title,message,NormalizeAnnouncementLevel(announcementLevel),(announcementImageUrl??"").Trim(),
            (announcementButtonText??"").Trim(),(announcementButtonUrl??"").Trim(),
            announcementStartsUtc?.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture)??"",
            announcementEndsUtc?.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture)??"");
        if (!string.Equals(oldRevision,newRevision,StringComparison.Ordinal))
            SetMeta(c,"announcement_id",string.IsNullOrWhiteSpace(title)&&string.IsNullOrWhiteSpace(message)?"":Guid.NewGuid().ToString("N"));
    }

    private static DateTimeOffset? ParseUtc(string? value) => DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var parsed)?parsed:null;
    private static string NormalizeAnnouncementLevel(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "WARNING" => "WARNING", "CRITICAL" => "CRITICAL", "UPDATE" => "UPDATE", _ => "INFO"
    };

    private static string? GetMeta(SqliteConnection c, string key)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM server_meta WHERE key=$k LIMIT 1";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar()?.ToString();
    }

    private static void SetMeta(SqliteConnection c, string key, string value)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO server_meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static void EnsureMeta(SqliteConnection c, string key, string value)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO server_meta(key,value) VALUES($k,$v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection c, string table, string name, string definition)
    {
        using var check = c.CreateCommand(); check.CommandText = $"PRAGMA table_info({table})";
        using var r = check.ExecuteReader(); var exists = false;
        while (r.Read()) if (r.GetString(1).Equals(name, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
        r.Close();
        if (exists) return;
        using var alter = c.CreateCommand(); alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {name} {definition}"; alter.ExecuteNonQuery();
    }

    private static void ApplyMigration(SqliteConnection c, int version, string name, Action apply)
    {
        using (var check = c.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM schema_migrations WHERE version=$v LIMIT 1";
            check.Parameters.AddWithValue("$v", version);
            if (check.ExecuteScalar() is not null) return;
        }
        apply();
        using var record = c.CreateCommand();
        record.CommandText = "INSERT INTO schema_migrations(version,name,applied_utc) VALUES($v,$n,$u)";
        record.Parameters.AddWithValue("$v", version); record.Parameters.AddWithValue("$n", name);
        record.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("O")); record.ExecuteNonQuery();
    }

    public int GetSchemaVersion()
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT COALESCE(MAX(version),0) FROM schema_migrations";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void CreateAuthSession(string id, DateTimeOffset expires) { using var c=Open(); using(var cleanup=c.CreateCommand()){cleanup.CommandText="DELETE FROM auth_sessions WHERE expires_utc < $cutoff OR (used_utc IS NOT NULL AND used_utc < $cutoff)";cleanup.Parameters.AddWithValue("$cutoff",DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));cleanup.ExecuteNonQuery();} using var cmd=c.CreateCommand(); cmd.CommandText="INSERT INTO auth_sessions(session_id,expires_utc) VALUES($id,$exp)"; cmd.Parameters.AddWithValue("$id",id); cmd.Parameters.AddWithValue("$exp",expires.ToString("O")); cmd.ExecuteNonQuery(); }
    public AuthSession? GetAuthSession(string id) { using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT session_id,steam_id,expires_utc,used_utc FROM auth_sessions WHERE session_id=$id"; cmd.Parameters.AddWithValue("$id",id); using var r=cmd.ExecuteReader(); return r.Read()?new AuthSession(r.GetString(0),r.IsDBNull(1)?null:r.GetString(1),DateTimeOffset.Parse(r.GetString(2)),r.IsDBNull(3)?null:DateTimeOffset.Parse(r.GetString(3))):null; }
    public void MarkAuthSessionVerified(string id,string steamId){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE auth_sessions SET steam_id=$s WHERE session_id=$id AND used_utc IS NULL";cmd.Parameters.AddWithValue("$s",steamId);cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();}
    public bool TryConsumeAuthSession(string id,DateTimeOffset now){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE auth_sessions SET used_utc=$u WHERE session_id=$id AND used_utc IS NULL AND steam_id IS NOT NULL AND expires_utc>$u";cmd.Parameters.AddWithValue("$u",now.ToString("O"));cmd.Parameters.AddWithValue("$id",id);return cmd.ExecuteNonQuery()==1;}

    public void CreateManualEntitlement(string steamId, DateTimeOffset? expires, string note, string plan = EntitlementPlans.Standard)
    {
        var key = LicenseKey.Generate();
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="INSERT INTO licenses(key_hash,key_last4,status,steam_id,expires_utc,created_utc,note,source,purchase_utc,plan) VALUES($h,$l,'ACTIVE',$s,$e,$c,$n,'MANUAL',$c,$p)";
        cmd.Parameters.AddWithValue("$h",HashKey(key)); cmd.Parameters.AddWithValue("$l",key[^4..]); cmd.Parameters.AddWithValue("$s",steamId);
        cmd.Parameters.AddWithValue("$e",(object?)expires?.ToString("O")??DBNull.Value); cmd.Parameters.AddWithValue("$c",DateTimeOffset.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$n",note); cmd.Parameters.AddWithValue("$p",EntitlementPlans.Normalize(plan)); cmd.ExecuteNonQuery();
    }

    public string? GetShopierOrderStatus(string orderId)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT status FROM shopier_orders WHERE order_id=$o LIMIT 1";
        cmd.Parameters.AddWithValue("$o",orderId);
        return cmd.ExecuteScalar()?.ToString();
    }

    public ShopierOrderRecord? GetShopierOrder(string orderId)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT order_id,steam_id,product_id,product_title,payment_status,order_note,status,error,license_id,received_utc FROM shopier_orders WHERE order_id=$o LIMIT 1";
        cmd.Parameters.AddWithValue("$o",orderId);
        using var r=cmd.ExecuteReader();
        return r.Read()?new ShopierOrderRecord(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.IsDBNull(8)?null:r.GetInt64(8),DateTimeOffset.Parse(r.GetString(9))):null;
    }

    public bool HasShopierOrder(string orderId) => GetShopierOrderStatus(orderId) is not null;

    public bool MarkShopierOrderRetryPending(string orderId)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="UPDATE shopier_orders SET status='RETRY_PENDING',error='' WHERE order_id=$o AND status='REJECTED'";
        cmd.Parameters.AddWithValue("$o",orderId);
        return cmd.ExecuteNonQuery()==1;
    }

    public LicenseRecord? ApplyShopierOrder(ParsedShopierOrder order, int licenseDays, TimeSpan gracePeriod, string plan = EntitlementPlans.Standard)
    {
        var now=DateTimeOffset.UtcNow;
        RefreshScheduledPlans(now);
        using var c=Open(); using var tx=c.BeginTransaction();
        using (var existingOrder = c.CreateCommand())
        {
            existingOrder.Transaction=tx; existingOrder.CommandText="SELECT license_id FROM shopier_orders WHERE order_id=$o"; existingOrder.Parameters.AddWithValue("$o",order.OrderId);
            var existingId=existingOrder.ExecuteScalar();
            if (existingId is long id) { tx.Commit(); return GetLicense(id); }
        }

        long licenseId;
        using (var find=c.CreateCommand())
        {
            find.Transaction=tx; find.CommandText="SELECT id,expires_utc,status,plan,next_plan,plan_changes_utc FROM licenses WHERE steam_id=$s AND status IN ('ACTIVE','EXPIRED') ORDER BY id DESC LIMIT 1"; find.Parameters.AddWithValue("$s",order.SteamId);
            using var r=find.ExecuteReader();
            if (r.Read())
            {
                licenseId=r.GetInt64(0);
                DateTimeOffset? currentExp=r.IsDBNull(1)?null:DateTimeOffset.Parse(r.GetString(1));
                var currentPlan=EntitlementPlans.Normalize(r.IsDBNull(3)?EntitlementPlans.Standard:r.GetString(3));
                var existingNextPlan=r.IsDBNull(4)?null:EntitlementPlans.Normalize(r.GetString(4));
                DateTimeOffset? existingChangeUtc=r.IsDBNull(5)?null:DateTimeOffset.Parse(r.GetString(5));
                r.Close();

                var purchasedPlan=EntitlementPlans.Normalize(plan);
                var currentPeriodStillActive=currentExp is not null && currentExp.Value>now;
                var withinGrace=currentExp is not null && now<currentExp.Value.Add(gracePeriod);
                var baseDate=currentExp is not null && (currentExp.Value>now||withinGrace)?currentExp.Value:now;
                // Mevcut lifetime erisimi sureli bir urun satin alimi ile kisaltmayiz.
                DateTimeOffset? newExpires=currentExp is null||licenseDays<=0?null:baseDate.AddDays(licenseDays);

                string resultingPlan;
                string? nextPlan;
                DateTimeOffset? changeUtc;
                if (currentExp is null && currentPlan==EntitlementPlans.Premium && purchasedPlan==EntitlementPlans.Standard)
                {
                    // Lifetime PREMIUM, daha düşük lifetime STANDARD satın alımıyla yanlışlıkla
                    // düşürülmez. Lifetime erişimde dönem sonu olmadığı için downgrade planlanamaz.
                    resultingPlan=currentPlan;
                    nextPlan=null;
                    changeUtc=null;
                }
                else if (currentPeriodStillActive && currentPlan==EntitlementPlans.Premium && purchasedPlan==EntitlementPlans.Standard)
                {
                    // Mevcut ücretli planın kalan süresi yanmaz. Yeni ürün, mevcut dönem
                    // bittiginde otomatik devreye girecek plan olarak kaydedilir.
                    resultingPlan=currentPlan;
                    nextPlan=purchasedPlan;
                    changeUtc=existingNextPlan is null?currentExp:existingChangeUtc??currentExp;
                }
                else
                {
                    // Donem bittiyse yeni urun hemen aktif olur. Kullanici bekleyen downgrade'i
                    // yeniden mevcut planla yenilerse bekleyen gecis de iptal edilir.
                    resultingPlan=purchasedPlan;
                    nextPlan=null;
                    changeUtc=null;
                }

                using var upd=c.CreateCommand(); upd.Transaction=tx;
                upd.CommandText="UPDATE licenses SET expires_utc=$e,status='ACTIVE',status_reason='',plan=$plan,next_plan=$next,plan_changes_utc=$change,shopier_order_id=$o,shopier_product_id=$product,purchase_utc=$purchase WHERE id=$id";
                upd.Parameters.AddWithValue("$e",(object?)newExpires?.ToString("O")??DBNull.Value);
                upd.Parameters.AddWithValue("$plan",resultingPlan); upd.Parameters.AddWithValue("$next",(object?)nextPlan??DBNull.Value);
                upd.Parameters.AddWithValue("$change",(object?)changeUtc?.ToString("O")??DBNull.Value);
                upd.Parameters.AddWithValue("$o",order.OrderId); upd.Parameters.AddWithValue("$product",order.ProductId);
                upd.Parameters.AddWithValue("$purchase",order.PurchaseUtc.ToString("O")); upd.Parameters.AddWithValue("$id",licenseId); upd.ExecuteNonQuery();
            }
            else
            {
                r.Close();
                var key=LicenseKey.Generate();
                using var ins=c.CreateCommand(); ins.Transaction=tx;
                var expires=licenseDays<=0?(DateTimeOffset?)null:now.AddDays(licenseDays);
                ins.CommandText="INSERT INTO licenses(key_hash,key_last4,status,steam_id,expires_utc,created_utc,note,source,shopier_order_id,shopier_product_id,purchase_utc,plan) VALUES($h,$l,'ACTIVE',$s,$e,$c,$n,'SHOPIER',$o,$p,$c,$plan); SELECT last_insert_rowid();";
                ins.Parameters.AddWithValue("$h",HashKey(key)); ins.Parameters.AddWithValue("$l",key[^4..]); ins.Parameters.AddWithValue("$s",order.SteamId); ins.Parameters.AddWithValue("$e",(object?)expires?.ToString("O")??DBNull.Value);
                ins.Parameters.AddWithValue("$c",now.ToString("O")); ins.Parameters.AddWithValue("$n","Shopier order "+order.OrderId); ins.Parameters.AddWithValue("$o",order.OrderId); ins.Parameters.AddWithValue("$p",order.ProductId); ins.Parameters.AddWithValue("$plan",EntitlementPlans.Normalize(plan));
                licenseId=(long)(ins.ExecuteScalar()??0L);
            }
        }

        using (var ord=c.CreateCommand())
        {
            ord.Transaction=tx; ord.CommandText="INSERT INTO shopier_orders(order_id,steam_id,product_id,product_title,payment_status,order_note,status,error,license_id,received_utc) VALUES($o,$s,$p,$t,$pay,$n,'ACCEPTED','',$l,$r) ON CONFLICT(order_id) DO UPDATE SET steam_id=excluded.steam_id,product_id=excluded.product_id,product_title=excluded.product_title,payment_status=excluded.payment_status,order_note=excluded.order_note,status='ACCEPTED',error='',license_id=excluded.license_id,received_utc=excluded.received_utc";
            ord.Parameters.AddWithValue("$o",order.OrderId); ord.Parameters.AddWithValue("$s",order.SteamId); ord.Parameters.AddWithValue("$p",order.ProductId); ord.Parameters.AddWithValue("$t",order.ProductTitle); ord.Parameters.AddWithValue("$pay",order.PaymentStatus); ord.Parameters.AddWithValue("$n",order.Note); ord.Parameters.AddWithValue("$l",licenseId); ord.Parameters.AddWithValue("$r",now.ToString("O")); ord.ExecuteNonQuery();
        }
        tx.Commit(); return GetLicense(licenseId);
    }

    public void RecordShopierOrder(ShopierOrderRecord o)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="INSERT INTO shopier_orders(order_id,steam_id,product_id,product_title,payment_status,order_note,status,error,license_id,received_utc) VALUES($o,$s,$p,$t,$pay,$n,$st,$e,$l,$r) ON CONFLICT(order_id) DO UPDATE SET steam_id=excluded.steam_id,product_id=excluded.product_id,product_title=excluded.product_title,payment_status=excluded.payment_status,order_note=excluded.order_note,status=excluded.status,error=excluded.error,license_id=excluded.license_id,received_utc=excluded.received_utc WHERE shopier_orders.status NOT IN ('ACCEPTED','IGNORED_INITIAL_SYNC')";
        cmd.Parameters.AddWithValue("$o",o.OrderId); cmd.Parameters.AddWithValue("$s",o.SteamId); cmd.Parameters.AddWithValue("$p",o.ProductId); cmd.Parameters.AddWithValue("$t",o.ProductTitle); cmd.Parameters.AddWithValue("$pay",o.PaymentStatus); cmd.Parameters.AddWithValue("$n",o.OrderNote); cmd.Parameters.AddWithValue("$st",o.Status); cmd.Parameters.AddWithValue("$e",o.Error); cmd.Parameters.AddWithValue("$l",(object?)o.LicenseId??DBNull.Value); cmd.Parameters.AddWithValue("$r",o.ReceivedUtc.ToString("O")); cmd.ExecuteNonQuery();
    }

    public List<ShopierOrderRecord> ListShopierOrders(int take)
    {
        using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT order_id,steam_id,product_id,product_title,payment_status,order_note,status,error,license_id,received_utc FROM shopier_orders ORDER BY received_utc DESC LIMIT $t"; cmd.Parameters.AddWithValue("$t",take);
        using var r=cmd.ExecuteReader(); var list=new List<ShopierOrderRecord>(); while(r.Read()) list.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.IsDBNull(8)?null:r.GetInt64(8),DateTimeOffset.Parse(r.GetString(9)))); return list;
    }

    public void RecordAdminAudit(string actor, string action, string target, string detail, string remoteIp)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="INSERT INTO admin_audit(created_utc,actor,action,target,detail,remote_ip) VALUES($c,$a,$x,$t,$d,$ip)";
        cmd.Parameters.AddWithValue("$c",DateTimeOffset.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$a",actor??"");
        cmd.Parameters.AddWithValue("$x",action??""); cmd.Parameters.AddWithValue("$t",target??"");
        cmd.Parameters.AddWithValue("$d",detail??""); cmd.Parameters.AddWithValue("$ip",remoteIp??""); cmd.ExecuteNonQuery();
    }

    public List<AdminAuditRecord> ListAdminAudit(int take)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT id,created_utc,actor,action,target,detail,remote_ip FROM admin_audit ORDER BY id DESC LIMIT $t";
        cmd.Parameters.AddWithValue("$t",Math.Clamp(take,1,1000)); using var r=cmd.ExecuteReader(); var list=new List<AdminAuditRecord>();
        while(r.Read()) list.Add(new(r.GetInt64(0),DateTimeOffset.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6)));
        return list;
    }

    public LicenseRecord? FindLatestLicenseBySteamId(string steamId){RefreshScheduledPlans(DateTimeOffset.UtcNow);using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=LicenseSelect+" WHERE steam_id=$s ORDER BY id DESC LIMIT 1";cmd.Parameters.AddWithValue("$s",steamId);using var r=cmd.ExecuteReader();return r.Read()?ReadLicense(r):null;}
    public LicenseRecord? FindLicenseByActivationToken(string raw){RefreshScheduledPlans(DateTimeOffset.UtcNow);using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=LicenseSelect+" WHERE activation_token_hash=$h";cmd.Parameters.AddWithValue("$h",HashKey(raw));using var r=cmd.ExecuteReader();return r.Read()?ReadLicense(r):null;}
    public LicenseRecord? GetLicense(long id){RefreshScheduledPlans(DateTimeOffset.UtcNow);using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=LicenseSelect+" WHERE id=$id";cmd.Parameters.AddWithValue("$id",id);using var r=cmd.ExecuteReader();return r.Read()?ReadLicense(r):null;}
    public bool TryActivateLicense(long id,string steam,string device,string? legacyDevice,string tokenHash,string version,DateTimeOffset now)
    {
        using var c=Open(); using var tx=c.BeginTransaction(); using var cmd=c.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
UPDATE licenses
SET steam_id=COALESCE(steam_id,$s),
    device_hash=$d,
    activation_token_hash=$t,
    first_activation_utc=COALESCE(first_activation_utc,$n),
    last_seen_utc=$n,
    last_client_version=$v
WHERE id=$id
  AND (steam_id IS NULL OR steam_id=$s)
  AND (device_hash IS NULL OR lower(device_hash)=lower($d) OR ($legacy<>'' AND lower(device_hash)=lower($legacy)))
  AND status='ACTIVE'
""";
        cmd.Parameters.AddWithValue("$s",steam); cmd.Parameters.AddWithValue("$d",device);
        cmd.Parameters.AddWithValue("$legacy",legacyDevice??""); cmd.Parameters.AddWithValue("$t",tokenHash);
        cmd.Parameters.AddWithValue("$n",now.ToString("O")); cmd.Parameters.AddWithValue("$v",version); cmd.Parameters.AddWithValue("$id",id);
        var changed=cmd.ExecuteNonQuery()==1;
        if (changed) tx.Commit(); else tx.Rollback();
        return changed;
    }

    // Test/maintenance helper. Production activation uses TryActivateLicense above.
    public void ActivateLicense(long id,string steam,string device,string tokenHash,string version,DateTimeOffset now)
    {
        if (!TryActivateLicense(id,steam,device,null,tokenHash,version,now))
            throw new InvalidOperationException("Lisans aktivasyonu atomik cihaz/Steam kontrolünü geçemedi.");
    }
    public void TouchLicense(long id,string version,DateTimeOffset now){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE licenses SET last_seen_utc=$n,last_client_version=$v WHERE id=$id";cmd.Parameters.AddWithValue("$n",now.ToString("O"));cmd.Parameters.AddWithValue("$v",version);cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();}
    public bool TryUpdateDeviceHash(long id,string expectedDevice,string newDevice)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="UPDATE licenses SET device_hash=$d WHERE id=$id AND lower(device_hash)=lower($expected)";
        cmd.Parameters.AddWithValue("$d",newDevice); cmd.Parameters.AddWithValue("$expected",expectedDevice); cmd.Parameters.AddWithValue("$id",id);
        return cmd.ExecuteNonQuery()==1;
    }
    public void UpdateDeviceHash(long id,string device){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE licenses SET device_hash=$d WHERE id=$id";cmd.Parameters.AddWithValue("$d",device);cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();}
    public List<LicenseRecord> ListLicenses(int take){RefreshScheduledPlans(DateTimeOffset.UtcNow);using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=LicenseSelect+" ORDER BY id DESC LIMIT $t";cmd.Parameters.AddWithValue("$t",take);using var r=cmd.ExecuteReader();var list=new List<LicenseRecord>();while(r.Read())list.Add(ReadLicense(r));return list;}
    public (int Total,int Active,int Suspended,int Revoked) GetStats(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*),SUM(CASE WHEN status='ACTIVE' THEN 1 ELSE 0 END),SUM(CASE WHEN status='SUSPENDED' THEN 1 ELSE 0 END),SUM(CASE WHEN status='REVOKED' THEN 1 ELSE 0 END) FROM licenses";using var r=cmd.ExecuteReader();r.Read();return(r.GetInt32(0),r.IsDBNull(1)?0:r.GetInt32(1),r.IsDBNull(2)?0:r.GetInt32(2),r.IsDBNull(3)?0:r.GetInt32(3));}
    public void SetLicenseStatus(long id,string status,string reason="")
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        var normalized=(status??"").Trim().ToUpperInvariant();
        var storedReason=normalized is "SUSPENDED" or "REVOKED" ? (reason??"").Trim() : "";
        cmd.CommandText="UPDATE licenses SET status=$v,status_reason=$r WHERE id=$id";
        cmd.Parameters.AddWithValue("$v",normalized); cmd.Parameters.AddWithValue("$r",storedReason); cmd.Parameters.AddWithValue("$id",id); cmd.ExecuteNonQuery();
    }
    public void SetLicensePlan(long id,string plan){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE licenses SET plan=$v,next_plan=NULL,plan_changes_utc=NULL WHERE id=$id";cmd.Parameters.AddWithValue("$v",EntitlementPlans.Normalize(plan));cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();}
    public void RefreshScheduledPlans(DateTimeOffset now){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE licenses SET plan=next_plan,next_plan=NULL,plan_changes_utc=NULL WHERE next_plan IS NOT NULL AND plan_changes_utc IS NOT NULL AND plan_changes_utc<=$n";cmd.Parameters.AddWithValue("$n",now.ToString("O"));cmd.ExecuteNonQuery();}
    public void ResetDevice(long id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE licenses SET device_hash=NULL,activation_token_hash=NULL WHERE id=$id";cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();}
    public void ResetSteam(long id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE licenses SET steam_id=NULL,device_hash=NULL,activation_token_hash=NULL WHERE id=$id";cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();}
    public bool DeleteRevokedLicense(long id)
    {
        using var c=Open(); using var tx=c.BeginTransaction();
        using (var detach=c.CreateCommand())
        {
            detach.Transaction=tx;
            detach.CommandText="UPDATE shopier_orders SET license_id=NULL WHERE license_id=$id";
            detach.Parameters.AddWithValue("$id",id);
            detach.ExecuteNonQuery();
        }
        using var delete=c.CreateCommand();
        delete.Transaction=tx;
        delete.CommandText="DELETE FROM licenses WHERE id=$id AND status='REVOKED'";
        delete.Parameters.AddWithValue("$id",id);
        var changed=delete.ExecuteNonQuery()==1;
        if(changed) tx.Commit(); else tx.Rollback();
        return changed;
    }
    public void ExpireIfPastGrace(long id, TimeSpan gracePeriod, DateTimeOffset now)
    {
        var l = GetLicense(id);
        if (l is null || l.ExpiresUtc is null || l.Status != "ACTIVE") return;
        if (now < l.ExpiresUtc.Value.Add(gracePeriod)) return;
        using var c=Open(); using var cmd=c.CreateCommand();
        // Sure bitince aktivasyon bagini hemen silmeyiz. Token/device yetki vermez;
        // LICENSE/PURCHASE kontrolu once statusu reddeder. Boylece client sonraki acilista da
        // "sure doldu -> yenile" durumunu ve destek/satin alma aksiyonlarini kesin olarak gorebilir.
        cmd.CommandText="UPDATE licenses SET status='EXPIRED',status_reason='' WHERE id=$id";
        cmd.Parameters.AddWithValue("$id",id); cmd.ExecuteNonQuery();
    }
    public void AddDays(long id,int days){var l=GetLicense(id);if(l is null)return;if(l.ExpiresUtc is null)return;var now=DateTimeOffset.UtcNow;var baseDate=l.ExpiresUtc.Value>now?l.ExpiresUtc.Value:now;using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE licenses SET expires_utc=$e,status='ACTIVE',status_reason='' WHERE id=$id";cmd.Parameters.AddWithValue("$e",baseDate.AddDays(days).ToString("O"));cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();}
    private void Exec(string sql,long id,string value){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.Parameters.AddWithValue("$v",value);cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();}

    public void UpsertRelease(ReleaseRecord rel){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO releases(version,mandatory,minimum_version,file_name,sha256,signature,notes,created_utc) VALUES($v,$m,$min,$f,$s,$sig,$n,$c) ON CONFLICT(version) DO UPDATE SET mandatory=excluded.mandatory,minimum_version=excluded.minimum_version,file_name=excluded.file_name,sha256=excluded.sha256,signature=excluded.signature,notes=excluded.notes,created_utc=excluded.created_utc";cmd.Parameters.AddWithValue("$v",rel.Version);cmd.Parameters.AddWithValue("$m",rel.Mandatory?1:0);cmd.Parameters.AddWithValue("$min",rel.MinimumVersion);cmd.Parameters.AddWithValue("$f",rel.FileName);cmd.Parameters.AddWithValue("$s",rel.Sha256);cmd.Parameters.AddWithValue("$sig",rel.Signature);cmd.Parameters.AddWithValue("$n",rel.Notes);cmd.Parameters.AddWithValue("$c",rel.CreatedUtc.ToString("O"));cmd.ExecuteNonQuery();}
    public ReleaseRecord? GetLatestRelease(){var all=ListReleases();return all.OrderByDescending(x=>ParseVersion(x.Version)).FirstOrDefault();}
    public ReleaseRecord? GetRelease(string version){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT version,mandatory,minimum_version,file_name,sha256,signature,notes,created_utc FROM releases WHERE version=$v";cmd.Parameters.AddWithValue("$v",version);using var r=cmd.ExecuteReader();return r.Read()?ReadRelease(r):null;}
    public List<ReleaseRecord> ListReleases(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT version,mandatory,minimum_version,file_name,sha256,signature,notes,created_utc FROM releases";using var r=cmd.ExecuteReader();var list=new List<ReleaseRecord>();while(r.Read())list.Add(ReadRelease(r));return list.OrderByDescending(x=>ParseVersion(x.Version)).ToList();}
    public bool DeleteRelease(string version){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="DELETE FROM releases WHERE version=$v";cmd.Parameters.AddWithValue("$v",version);return cmd.ExecuteNonQuery()==1;}
    private static Version ParseVersion(string s)=>Version.TryParse(s,out var v)?v:new Version(0,0);

    private const string LicenseSelect="SELECT id,key_last4,status,steam_id,device_hash,expires_utc,created_utc,first_activation_utc,last_seen_utc,last_client_version,note,source,shopier_order_id,shopier_product_id,purchase_utc,plan,next_plan,plan_changes_utc,status_reason FROM licenses";
    private static LicenseRecord ReadLicense(SqliteDataReader r)=>new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.IsDBNull(5)?null:DateTimeOffset.Parse(r.GetString(5)),DateTimeOffset.Parse(r.GetString(6)),r.IsDBNull(7)?null:DateTimeOffset.Parse(r.GetString(7)),r.IsDBNull(8)?null:DateTimeOffset.Parse(r.GetString(8)),r.GetString(9),r.GetString(10),r.IsDBNull(11)?"LEGACY":r.GetString(11),r.IsDBNull(12)?null:r.GetString(12),r.IsDBNull(13)?null:r.GetString(13),r.IsDBNull(14)?null:DateTimeOffset.Parse(r.GetString(14)),r.IsDBNull(15)?EntitlementPlans.Standard:EntitlementPlans.Normalize(r.GetString(15)),r.IsDBNull(16)?null:EntitlementPlans.Normalize(r.GetString(16)),r.IsDBNull(17)?null:DateTimeOffset.Parse(r.GetString(17)),r.IsDBNull(18)?"":r.GetString(18));
    private static ReleaseRecord ReadRelease(SqliteDataReader r)=>new(r.GetString(0),r.GetInt32(1)==1,r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),DateTimeOffset.Parse(r.GetString(7)));
    private static string HashKey(string s)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
