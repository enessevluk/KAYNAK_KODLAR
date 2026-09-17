using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RavenMapPanel;

public enum LicenseCheckState { Valid, ValidOffline, Missing, PurchaseRequired, Invalid, ServerUnavailable }
public static class EntitlementPlan
{
    public const string Standard = "STANDARD";
    public const string Premium = "PREMIUM";
    public static string Normalize(string? value) => string.Equals(value, Premium, StringComparison.OrdinalIgnoreCase) ? Premium : Standard;
}
public sealed record UserAction(string Id, string Label, string Url, bool Primary);
public sealed record UserIssue(string Code, string Title, string Message, string Detail, string Severity, IReadOnlyList<UserAction> Actions);
public sealed record LicenseCheckResult(LicenseCheckState State, string Message, string SteamId = "", DateTimeOffset? ExpiresUtc = null, string Plan = EntitlementPlan.Standard, string? NextPlan = null, DateTimeOffset? PlanChangesUtc = null, UserIssue? Issue = null, DateTimeOffset? GraceEndsUtc = null)
{
    public bool IsValid => State is LicenseCheckState.Valid or LicenseCheckState.ValidOffline;
}
public sealed record SteamLoginSession(string SessionId, string LoginUrl);
public sealed record SteamLoginStatus(string Status, string? SteamId);
public sealed record StoreInfo(bool Enabled, string BuyUrl, string ProductName, string PurchaseHint, string SupportUrl = "", string SupportLabel = "Discord Destek");
public sealed record UpdateInfo(bool UpdateAvailable, bool Mandatory, string CurrentVersion, string LatestVersion, string Notes, string Sha256, string Signature);
public sealed record SubscriptionNotice(string Key, string Title, string Message, bool IsGracePeriod);
public sealed record ServerInfo(string InstanceId, string ServerVersion, int ProtocolVersion, int TotalEntitlements, int ActiveEntitlements, int DatabaseSchemaVersion);
public sealed record BootstrapInfo(int SchemaVersion, bool Maintenance, string MaintenanceMessage, string AnnouncementId, string AnnouncementTitle, string AnnouncementMessage,
    string AnnouncementLevel,string AnnouncementImageUrl,string AnnouncementButtonText,string AnnouncementButtonUrl,
    DateTimeOffset? AnnouncementStartsUtc,DateTimeOffset? AnnouncementEndsUtc,string MinimumClientVersion,DateTimeOffset ServerTimeUtc,
    string HomeMediaUrls = "");

internal sealed class LicenseCache
{
    public string Provider { get; set; } = "";
    public string LicenseId { get; set; } = "";
    public string Product { get; set; } = "";
    public string ActivationToken { get; set; } = "";
    public string SteamId { get; set; } = "";
    public DateTimeOffset? ExpiresUtc { get; set; }
    public DateTimeOffset? GraceEndsUtc { get; set; }
    public string Plan { get; set; } = EntitlementPlan.Standard;
    public string? NextPlan { get; set; }
    public DateTimeOffset? PlanChangesUtc { get; set; }
    public string OfflineToken { get; set; } = "";
    public DateTimeOffset LastOnlineValidationUtc { get; set; }
    public string LastSubscriptionNoticeKey { get; set; } = "";
    public string ServerInstanceId { get; set; } = "";
}

public sealed class LicenseService
{
    private readonly RavenServerConfig config;
    private readonly HttpClient http;
    private readonly HttpClient licenseHttp;
    private readonly string deviceHash = DeviceIdentity.GetDeviceHash();
    private readonly string legacyDeviceHash = DeviceIdentity.GetLegacyDeviceHash();
    private readonly JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true };
    private bool onlineValidatedThisSession;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RavenMapPanel-License-v1");
    private static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RavenMapPanel");
    public static string CachePath => Path.Combine(CacheRoot, "license.dat");
    private static string StoreInfoCachePath => Path.Combine(CacheRoot, "store-info.json");
    private static string AnnouncementStatePath => Path.Combine(CacheRoot, "announcement.seen");
    public string DeviceHash => deviceHash;
    public string LicenseProduct => NormalizeLicenseProduct(config.LicenseProduct);
    public string LicenseServerId => "map-" + deviceHash;
    public string CurrentVersion => (Assembly.GetEntryAssembly() ?? typeof(LicenseService).Assembly).GetName().Version?.ToString(3) ?? "1.0.0";
    public bool OnlineValidatedThisSession => onlineValidatedThisSession;
    public RavenServerConfig ServerConfig => config;

    public LicenseService()
    {
        config = RavenServerConfig.Load();
        http = new HttpClient { BaseAddress = config.GetBaseUri(), Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RavenMapPanel/{CurrentVersion}");
        licenseHttp = new HttpClient { BaseAddress = config.GetLicenseCenterUri(), Timeout = TimeSpan.FromSeconds(12) };
        licenseHttp.DefaultRequestHeaders.UserAgent.ParseAdd($"RavenMapPanel/{CurrentVersion}");
    }

    public async Task<LicenseCheckResult> ValidateCachedAsync(bool allowOffline = true, CancellationToken ct = default)
    {
        var cache = LoadCache();
        if (cache is null || string.IsNullOrWhiteSpace(cache.ActivationToken))
            return new(LicenseCheckState.Missing, "Raven Map lisans anahtarı henüz etkinleştirilmedi.");

        if (!cache.Provider.Equals("license-center", StringComparison.OrdinalIgnoreCase) ||
            !cache.Product.Equals(LicenseProduct, StringComparison.OrdinalIgnoreCase))
            return new(LicenseCheckState.Missing, "Bu sürüm merkezi lisans anahtarı kullanır. Raven Map lisans anahtarınızı girerek yeniden etkinleştirin.");

        try
        {
            using var res = await licenseHttp.PostAsJsonAsync("api/license/validate", new
            {
                token = cache.ActivationToken,
                installId = deviceHash,
                serverId = LicenseServerId,
                product = LicenseProduct
            }, ct);
            if (!res.IsSuccessStatusCode)
            {
                var err = await ReadLicenseCenterErrorAsync(res, ct);
                return new(LicenseCheckState.Invalid, err.Message, cache.LicenseId, cache.ExpiresUtc, cache.Plan, Issue: err.ToIssue());
            }
            var dto = await res.Content.ReadFromJsonAsync<LicenseCenterDto>(json, ct) ?? throw new InvalidDataException("Merkezi lisans yanıtı okunamadı.");
            if (!dto.Ok || string.IsNullOrWhiteSpace(dto.Token) || string.IsNullOrWhiteSpace(dto.LicenseId) ||
                !string.Equals(dto.Product, LicenseProduct, StringComparison.OrdinalIgnoreCase))
                return new(LicenseCheckState.Invalid, "Lisans merkezi Raven Map yetkisini onaylamadı.");

            cache.ActivationToken = dto.Token;
            cache.LicenseId = dto.LicenseId;
            cache.SteamId = dto.LicenseId;
            cache.ExpiresUtc = dto.ExpiresUtc;
            cache.GraceEndsUtc = dto.GraceUntilUtc;
            cache.Plan = EntitlementPlan.Normalize(dto.Plan);
            cache.OfflineToken = "";
            cache.LastOnlineValidationUtc = DateTimeOffset.UtcNow;
            onlineValidatedThisSession = true;
            SaveCache(cache);
            return new(LicenseCheckState.Valid, "Raven Map lisansı çevrimiçi doğrulandı.", cache.LicenseId, cache.ExpiresUtc, cache.Plan, GraceEndsUtc: cache.GraceEndsUtc);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var message = "Raven lisans merkezine ulaşılamadı. Çevrimdışı kullanım kapalıdır; internet bağlantısı olmadan harita üretilemez.";
            return new(LicenseCheckState.ServerUnavailable, message, cache.LicenseId, cache.ExpiresUtc, cache.Plan, Issue: CreateLocalUnavailableIssue(message));
        }
    }

    public async Task<LicenseCheckResult> ActivateLicenseKeyAsync(string activationKey, CancellationToken ct = default)
    {
        activationKey = (activationKey ?? "").Trim();
        if (activationKey.Length < 20 || activationKey.Length > 160)
            return new(LicenseCheckState.Invalid, "Lisans anahtarı biçimi geçersiz.");

        try
        {
            using var res = await licenseHttp.PostAsJsonAsync("api/license/activate", new
            {
                activationKey,
                installId = deviceHash,
                serverId = LicenseServerId,
                product = LicenseProduct
            }, ct);
            if (!res.IsSuccessStatusCode)
            {
                var err = await ReadLicenseCenterErrorAsync(res, ct);
                return new(LicenseCheckState.Invalid, err.Message, Issue: err.ToIssue());
            }

            var dto = await res.Content.ReadFromJsonAsync<LicenseCenterDto>(json, ct) ?? throw new InvalidDataException("Aktivasyon yanıtı okunamadı.");
            if (!dto.Ok || string.IsNullOrWhiteSpace(dto.Token) || string.IsNullOrWhiteSpace(dto.LicenseId) ||
                !string.Equals(dto.Product, LicenseProduct, StringComparison.OrdinalIgnoreCase))
                return new(LicenseCheckState.Invalid, "Bu anahtar Raven Map ürünü için yetkili değil.");

            var cache = new LicenseCache
            {
                Provider = "license-center",
                LicenseId = dto.LicenseId,
                Product = LicenseProduct,
                ActivationToken = dto.Token,
                SteamId = dto.LicenseId,
                ExpiresUtc = dto.ExpiresUtc,
                GraceEndsUtc = dto.GraceUntilUtc,
                Plan = EntitlementPlan.Normalize(dto.Plan),
                OfflineToken = "",
                LastOnlineValidationUtc = DateTimeOffset.UtcNow
            };
            SaveCache(cache);
            onlineValidatedThisSession = true;
            return new(LicenseCheckState.Valid, "Lisans anahtarı doğrulandı. Raven Map etkinleştirildi.", cache.LicenseId, cache.ExpiresUtc, cache.Plan, GraceEndsUtc: cache.GraceEndsUtc);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var message = "Raven lisans merkezine ulaşılamadı. İnternet bağlantınızı kontrol edin.";
            return new(LicenseCheckState.ServerUnavailable, message, Issue: CreateLocalUnavailableIssue(message));
        }
    }

    public async Task<SteamLoginSession> CreateSteamSessionAsync(CancellationToken ct = default)
    {
        using var res = await http.PostAsync("api/auth/session", null, ct);
        res.EnsureSuccessStatusCode();
        var session = await res.Content.ReadFromJsonAsync<SteamLoginSession>(json, ct)
                      ?? throw new InvalidDataException("Steam oturumu oluşturulamadı.");
        if (!Uri.TryCreate(session.LoginUrl, UriKind.Absolute, out var loginUri))
            throw new InvalidDataException("Raven Server geçersiz Steam giriş adresi döndürdü.");

        var serverBase = http.BaseAddress ?? config.GetBaseUri();
        var sameAuthority = string.Equals(loginUri.Host, serverBase.Host, StringComparison.OrdinalIgnoreCase)
                            && loginUri.Port == serverBase.Port;
        if (!sameAuthority || !loginUri.AbsolutePath.StartsWith("/auth/steam", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Raven Server beklenmeyen Steam giriş adresi döndürdü: {loginUri.GetLeftPart(UriPartial.Authority)}");

        // Test client HTTP ile calisiyorsa stale bir HTTPS scheme'ini ayni host/port icin duzelt.
        // Server tarafindaki callback de guncel surumde request origin'inden uretilir.
        if (config.AllowInsecureHttpForTesting && serverBase.Scheme == Uri.UriSchemeHttp && loginUri.Scheme != Uri.UriSchemeHttp)
            session = session with { LoginUrl = new Uri(serverBase, loginUri.PathAndQuery.TrimStart('/')).ToString() };
        else if (!string.Equals(loginUri.Scheme, serverBase.Scheme, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Raven Server ile Steam giriş adresinin HTTP/HTTPS ayarı uyuşmuyor.");

        return session;
    }

    public async Task<SteamLoginStatus> PollSteamSessionAsync(string sessionId, CancellationToken ct = default)
    {
        using var res = await http.GetAsync($"api/auth/session/{Uri.EscapeDataString(sessionId)}", ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<SteamLoginStatus>(json, ct) ?? new("pending", null);
    }

    public async Task<ServerInfo> GetServerInfoAsync(CancellationToken ct = default)
    {
        using var res = await http.GetAsync("api/server/info", ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<ServerInfo>(json, ct)
               ?? throw new InvalidDataException("Raven sunucu kimliği okunamadı.");
    }

    public async Task<BootstrapInfo> GetBootstrapAsync(CancellationToken ct = default)
    {
        // Raven Map merkezi lisans modunda ayrı Raven.Server kurulumu gerektirmez.
        // Duyuru/bootstrap servisi yoksa açılışı ağ zaman aşımına uğratmayız.
        if (LicenseProduct == "raven_map")
            return EmptyBootstrap();
        try
        {
            using var res = await http.GetAsync("api/bootstrap", ct);
            if (res.StatusCode == HttpStatusCode.NotFound)
                return EmptyBootstrap();
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadFromJsonAsync<BootstrapInfo>(json, ct)
                   ?? EmptyBootstrap();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Duyuru/bootstrap arizasi lisans hata ekranina ulasmayi engellememeli.
            return EmptyBootstrap();
        }
    }

    private static BootstrapInfo EmptyBootstrap() => new(0,false,"","","","","INFO","","","",null,null,"",DateTimeOffset.UtcNow);

    public bool ShouldShowAnnouncement(BootstrapInfo bootstrap)
    {
        if (string.IsNullOrWhiteSpace(bootstrap.AnnouncementId) ||
            (string.IsNullOrWhiteSpace(bootstrap.AnnouncementTitle) && string.IsNullOrWhiteSpace(bootstrap.AnnouncementMessage))) return false;
        try { return !string.Equals(File.Exists(AnnouncementStatePath)?File.ReadAllText(AnnouncementStatePath):"",bootstrap.AnnouncementId,StringComparison.Ordinal); }
        catch { return true; }
    }

    public void MarkAnnouncementShown(string announcementId)
    {
        if (string.IsNullOrWhiteSpace(announcementId)) return;
        try { Directory.CreateDirectory(CacheRoot); File.WriteAllText(AnnouncementStatePath,announcementId); } catch { }
    }

    public async Task<LicenseCheckResult> RequireOnlineEntitlementAsync(CancellationToken ct = default)
    {
        // Harita üretimi gibi kritik işlemler local/offline cache ile asla başlamaz.
        return await ValidateCachedAsync(allowOffline: false, ct);
    }

    public async Task<IReadOnlyList<CustomPrefabVariant>> RefreshPrefabCatalogAsync(CancellationToken ct = default)
    {
        var cache=LoadCache();
        if (cache is null || string.IsNullOrWhiteSpace(cache.ActivationToken))
            throw new InvalidOperationException("Özel prefab listesi için geçerli Raven oturumu bulunamadı.");
        if (cache.Provider.Equals("license-center", StringComparison.OrdinalIgnoreCase))
        {
            // Merkezi PHP lisansı yalnız yetkilendirme yapar. Raven.Server'a özel ücretli
            // prefab deposu bu modda zorunlu değildir; yerel/vanilla katalog kullanılmaya devam eder.
            CustomPrefabService.SetEntitledVariants([]);
            return [];
        }
        using var request=new HttpRequestMessage(HttpMethod.Get,"api/prefabs/catalog");
        request.Headers.Add("X-Raven-Activation",cache.ActivationToken);
        request.Headers.Add("X-Raven-Device",deviceHash);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseContentRead,ct);
        response.EnsureSuccessStatusCode();
        var envelope=await response.Content.ReadFromJsonAsync<PrefabCatalogEnvelopeDto>(json,ct)
            ?? throw new InvalidDataException("Özel prefab listesi okunamadı.");
        var payload=Convert.FromBase64String(envelope.Payload??"");
        var signature=Convert.FromBase64String(envelope.Signature??"");
        using (var rsa=RSA.Create())
        {
            rsa.ImportFromPem(SecurityKeys.LicensePublicKeyPem);
            if (!rsa.VerifyData(payload,signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))
                throw new CryptographicException("Özel prefab listesi Raven imza doğrulamasını geçemedi.");
        }
        var document=JsonSerializer.Deserialize<PrefabCatalogDocumentDto>(payload,json)
            ?? throw new InvalidDataException("Özel prefab listesi içeriği geçersiz.");
        var variants=(document.Variants??[]).Select(v => new CustomPrefabVariant
        {
            MonumentId=v.MonumentId??"",
            Id=v.Id??"",
            Name=$"[{EntitlementPlan.Normalize(v.MinimumPlan)}] {v.Name}",
            MinimumPlan=EntitlementPlan.Normalize(v.MinimumPlan),
            IsRemote=true,
            IsValid=(v.Targets?.Count??0)>0,
            ValidationMessage=$"Raven Server korumalı içerik · {EntitlementPlan.Normalize(v.MinimumPlan)}",
            Targets=(v.Targets??[]).Select(t => new CustomPrefabTarget
            {
                ContentId=t.ContentId??"",
                Sha256=t.Sha256??"",
                TargetFileName=t.TargetFileName??""
            }).ToList()
        }).Where(v => !string.IsNullOrWhiteSpace(v.MonumentId) && !string.IsNullOrWhiteSpace(v.Id)).ToList();
        CustomPrefabService.SetEntitledVariants(variants);
        return variants;
    }

    public async Task<byte[]> DownloadPrefabAsync(string contentId,string expectedSha256,CancellationToken ct=default)
    {
        var cache=LoadCache();
        if (cache is null || string.IsNullOrWhiteSpace(cache.ActivationToken))
            throw new InvalidOperationException("Özel prefab indirme yetkisi bulunamadı.");
        if (cache.Provider.Equals("license-center", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Uzak özel prefab deposu merkezi lisans modunda etkin değildir.");
        using var request=new HttpRequestMessage(HttpMethod.Get,$"api/prefabs/file/{Uri.EscapeDataString(contentId)}");
        request.Headers.Add("X-Raven-Activation",cache.ActivationToken);
        request.Headers.Add("X-Raven-Device",deviceHash);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 67108864)
            throw new InvalidDataException("Özel prefab güvenli boyut sınırını aşıyor.");
        var bytes=await response.Content.ReadAsByteArrayAsync(ct);
        var actual=Convert.ToHexString(SHA256.HashData(bytes));
        if (!actual.Equals(expectedSha256,StringComparison.OrdinalIgnoreCase))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new CryptographicException("İndirilen özel prefab bütünlük kontrolünü geçemedi.");
        }
        return bytes;
    }

    public void OpenSteamLogin(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    public bool OpenStore(string url)
    {
        var target = NormalizeExternalUrl(url);
        if (target is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? NormalizeExternalUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return null;

        // discord.gg bağlantıları bazı Windows/Discord kurulumlarında uygulamaya
        // yanlış aktarılabiliyor. Standart web davet adresi tarayıcıda daha güvenilir.
        if (uri.Host.Equals("discord.gg", StringComparison.OrdinalIgnoreCase))
        {
            var inviteCode = uri.AbsolutePath.Trim('/');
            if (!string.IsNullOrWhiteSpace(inviteCode) && !inviteCode.Contains('/'))
                return $"https://discord.com/invite/{inviteCode}";
        }

        return uri.AbsoluteUri;
    }

    public async Task<StoreInfo> GetStoreInfoAsync(CancellationToken ct = default)
    {
        if (LicenseProduct == "raven_map")
        {
            var buyUrl = NormalizeExternalUrl(config.StoreUrl) ?? "";
            var supportUrl = NormalizeExternalUrl(config.SupportUrl) ?? "";
            return new StoreInfo(!string.IsNullOrWhiteSpace(buyUrl), buyUrl, "Raven Map Panel",
                "Satın alma sonrasında verilen aktivasyon anahtarını kullanın.", supportUrl, "Destek");
        }
        try
        {
            using var res = await http.GetAsync("api/store/info", ct);
            res.EnsureSuccessStatusCode();
            var info = await res.Content.ReadFromJsonAsync<StoreInfo>(json, ct)
                       ?? new(false, "", "Raven Map Panel", "Sipariş notuna SteamID64 yazın.", "", "Discord Destek");
            SaveStoreInfoCache(info);
            return info;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Raven Server geçici olarak yokken daha önce alınmış satın alma/destek bağlantılarını kullan.
            return LoadStoreInfoCache()
                   ?? new(false, "", "Raven Map Panel", "Raven Server şu anda erişilemiyor.", "", "Discord Destek");
        }
    }

    public async Task<LicenseCheckResult> ActivateSteamEntitlementAsync(string authSessionId, CancellationToken ct = default)
    {
        try
        {
            using var res = await http.PostAsJsonAsync("api/access/activate", new
            {
                authSessionId,
                deviceHash,
                legacyDeviceHash,
                clientVersion = CurrentVersion
            }, ct);
            if (!res.IsSuccessStatusCode)
            {
                var err = await ReadErrorAsync(res, ct);
                var issue = err.ToIssue();
                if (res.StatusCode == HttpStatusCode.PaymentRequired || err.Code.Equals("PURCHASE_REQUIRED", StringComparison.OrdinalIgnoreCase))
                    return new(LicenseCheckState.PurchaseRequired, err.Message, Issue: issue);
                return new(LicenseCheckState.Invalid, err.Message, Issue: issue);
            }

            var dto = await res.Content.ReadFromJsonAsync<ActivateDto>(json, ct) ?? throw new InvalidDataException("Aktivasyon yanıtı okunamadı.");
            var cache = new LicenseCache
            {
                Provider = "raven-server",
                ActivationToken = dto.ActivationToken ?? "",
                SteamId = dto.SteamId ?? "",
                ExpiresUtc = dto.ExpiresUtc,
                GraceEndsUtc = dto.GraceEndsUtc,
                Plan = EntitlementPlan.Normalize(dto.Plan),
                NextPlan = string.IsNullOrWhiteSpace(dto.NextPlan) ? null : EntitlementPlan.Normalize(dto.NextPlan),
                PlanChangesUtc = dto.PlanChangesUtc,
                OfflineToken = dto.OfflineToken ?? "",
                ServerInstanceId = dto.ServerInstanceId ?? "",
                LastOnlineValidationUtc = DateTimeOffset.UtcNow
            };
            onlineValidatedThisSession = true;
            SaveCache(cache);
            return new(LicenseCheckState.Valid, "Satın alım doğrulandı. Raven Map Panel etkinleştirildi.", cache.SteamId, cache.ExpiresUtc, cache.Plan, cache.NextPlan, cache.PlanChangesUtc, GraceEndsUtc: cache.GraceEndsUtc);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var message = "Raven lisans sunucusuna ulaşılamadı.";
            return new(LicenseCheckState.ServerUnavailable, message, Issue: CreateLocalUnavailableIssue(message));
        }
    }

    public async Task<UpdateInfo> CheckUpdateAsync(CancellationToken ct = default)
    {
        if (LicenseProduct == "raven_map")
            return new(false, false, CurrentVersion, CurrentVersion, "", "", "");
        using var res = await http.GetAsync($"api/update/check?currentVersion={Uri.EscapeDataString(CurrentVersion)}", ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<UpdateInfo>(json, ct) ?? new(false, false, CurrentVersion, CurrentVersion, "", "", "");
    }

    public LicenseCacheSnapshot? GetCacheSnapshot()
    {
        var c = LoadCache();
        return c is null ? null : new(c.ActivationToken, c.SteamId, c.ExpiresUtc, c.Plan);
    }

    public SubscriptionNotice? GetPendingSubscriptionNotice(LicenseCheckResult result)
    {
        if (!result.IsValid || result.ExpiresUtc is null) return null;
        var now = DateTimeOffset.UtcNow;
        var expires = result.ExpiresUtc.Value;
        string key;
        string title;
        string message;
        bool grace = false;

        if (now >= expires)
        {
            key = "GRACE:" + expires.UtcDateTime.ToString("yyyyMMddHH");
            title = "Abonelik ek kullanım süresi";
            var graceEnds = result.GraceEndsUtc ?? LoadCache()?.GraceEndsUtc;
            if (graceEnds is null) return null;
            var left = graceEnds.Value - now;
            if (left <= TimeSpan.Zero) return null;
            message = $"Aboneliğiniz sona erdi. Ek kullanım süreniz aktif. Kalan ek süre: {FormatRemaining(left)}. Bu süre içinde yenileme yapmazsanız Raven Map Panel erişiminiz kapanacaktır.";
            grace = true;
        }
        else
        {
            var left = expires - now;
            int threshold;
            if (left <= TimeSpan.FromDays(1)) threshold = 1;
            else if (left <= TimeSpan.FromDays(3)) threshold = 3;
            else if (left <= TimeSpan.FromDays(7)) threshold = 7;
            else return null;
            key = $"WARN{threshold}:" + expires.UtcDateTime.ToString("yyyyMMddHH");
            title = "Abonelik süreniz azalıyor";
            message = $"Raven Map Panel aboneliğinizin bitmesine {FormatRemaining(left)} kaldı. Süreniz dolmadan yenilerseniz satın aldığınız yeni süre mevcut bitiş tarihinizin üzerine eklenir.";
        }

        var cache = LoadCache();
        if (cache is not null && cache.LastSubscriptionNoticeKey == key) return null;
        return new SubscriptionNotice(key, title, message, grace);
    }

    public void MarkSubscriptionNoticeShown(string key)
    {
        var cache = LoadCache();
        if (cache is null) return;
        cache.LastSubscriptionNoticeKey = key ?? "";
        SaveCache(cache);
    }

    private static string FormatRemaining(TimeSpan left)
    {
        if (left.TotalDays >= 1)
        {
            var days = Math.Max(0, (int)Math.Floor(left.TotalDays));
            var hours = Math.Max(0, left.Hours);
            return hours > 0 ? $"{days} gün {hours} saat" : $"{days} gün";
        }
        if (left.TotalHours >= 1) return $"{Math.Max(1, (int)Math.Ceiling(left.TotalHours))} saat";
        return $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} dakika";
    }

    private UserIssue CreateLocalUnavailableIssue(string message)
    {
        var store = LoadStoreInfoCache();
        var actions = new List<UserAction>();
        if (Uri.TryCreate(config.SupportUrl, UriKind.Absolute, out var configuredSupport) &&
            (configuredSupport.Scheme == Uri.UriSchemeHttps || configuredSupport.Scheme == Uri.UriSchemeHttp))
            actions.Add(new UserAction("SUPPORT", "Destek", config.SupportUrl, true));
        if (store is not null && Uri.TryCreate(store.SupportUrl, UriKind.Absolute, out var support) &&
            (support.Scheme == Uri.UriSchemeHttps || support.Scheme == Uri.UriSchemeHttp) &&
            actions.All(a => !a.Url.Equals(store.SupportUrl, StringComparison.OrdinalIgnoreCase)))
            actions.Add(new UserAction("SUPPORT", string.IsNullOrWhiteSpace(store.SupportLabel) ? "Discord Destek" : store.SupportLabel, store.SupportUrl, true));
        return new UserIssue(
            "SERVER_UNAVAILABLE",
            "Raven sunucusuna ulaşılamıyor",
            message,
            "İnternet bağlantınızı kontrol edip tekrar deneyin. Sorun Raven sunucusundaysa destek kanalımızdan durum bilgisi alabilirsiniz.",
            "warning",
            actions);
    }

    private StoreInfo? LoadStoreInfoCache()
    {
        try
        {
            if (!File.Exists(StoreInfoCachePath)) return null;
            return JsonSerializer.Deserialize<StoreInfo>(File.ReadAllText(StoreInfoCachePath), json);
        }
        catch { return null; }
    }

    private void SaveStoreInfoCache(StoreInfo info)
    {
        try
        {
            Directory.CreateDirectory(CacheRoot);
            File.WriteAllText(StoreInfoCachePath, JsonSerializer.Serialize(info, json));
        }
        catch { }
    }

    public void ClearActivation()
    {
        try { if (File.Exists(CachePath)) File.Delete(CachePath); } catch { }
    }

    private LicenseCache? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var protectedBytes = File.ReadAllBytes(CachePath);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<LicenseCache>(plain, json);
        }
        catch { return null; }
    }

    private void SaveCache(LicenseCache cache)
    {
        Directory.CreateDirectory(CacheRoot);
        var plain = JsonSerializer.SerializeToUtf8Bytes(cache, json);
        var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(CachePath, protectedBytes);
    }

    private static async Task<ErrorDto> ReadErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var e = await res.Content.ReadFromJsonAsync<ErrorDto>(cancellationToken: ct);
            if (e is not null && !string.IsNullOrWhiteSpace(e.Message)) return e;
        }
        catch { }
        var transient = res.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
        if (transient)
            return new(false, "SERVER_TEMPORARILY_UNAVAILABLE",
                "Raven sunucusu geçici olarak kullanılamıyor",
                "Sunucu güncelleniyor, yeniden başlatılıyor veya kısa süreli bağlantı sorunu yaşıyor.",
                "Bir iki dakika bekleyip yeniden deneyin. Sorun devam ederse destek bağlantısını kullanın.",
                "warning", null);

        var msg = res.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Raven oturumu geçersiz.",
            HttpStatusCode.PaymentRequired => "Bu Steam hesabına ait aktif Raven Map Panel satın alımı bulunamadı.",
            HttpStatusCode.TooManyRequests => "Kısa sürede çok fazla deneme yapıldı. Biraz bekleyip yeniden deneyin.",
            _ => "Raven isteği şu anda tamamlanamadı."
        };
        return new(false, "HTTP_" + (int)res.StatusCode, "İşlem tamamlanamadı", msg, "Tekrar deneyin. Aynı sorun devam ederse destek ekibine ulaşabilirsiniz.", "error", null);
    }

    private async Task<ErrorDto> ReadLicenseCenterErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        string code = "HTTP_" + (int)res.StatusCode;
        try
        {
            var envelope = await res.Content.ReadFromJsonAsync<LicenseCenterErrorDto>(json, ct);
            if (!string.IsNullOrWhiteSpace(envelope?.Code)) code = envelope.Code;
        }
        catch { }

        var message = code switch
        {
            "ACTIVATION_KEY_NOT_FOUND" or "ACTIVATION_KEY_INVALID" => "Lisans anahtarı bulunamadı veya geçersiz.",
            "PRODUCT_NOT_ENTITLED" => "Bu lisans Raven Map ürününü içermiyor.",
            "LICENSE_PRODUCT_DEVICE_LIMIT" => "Bu Raven Map lisansı başka bir cihaza bağlı. Owner panelinden cihaz bağını sıfırlayın.",
            "LICENSE_SERVER_BOUND_TO_OTHER_INSTALL" => "Bu Raven Map lisansı başka bir Windows kurulumuna bağlı.",
            "LICENSE_REVOKED" => "Raven Map lisansı iptal edilmiş.",
            "LICENSE_EXPIRED" => "Raven Map lisansının süresi dolmuş.",
            "TOKEN_SCOPE_MISMATCH" or "LICENSE_SCOPE_MISMATCH" => "Lisans bu cihazla eşleşmiyor. Cihaz bağını owner panelinden sıfırlayın.",
            "LICENSE_NOT_FOUND" => "Lisans kaydı artık mevcut değil.",
            _ when res.StatusCode == HttpStatusCode.TooManyRequests => "Çok fazla lisans denemesi yapıldı. Biraz bekleyip tekrar deneyin.",
            _ => "Raven lisans merkezi isteği reddetti (" + code + ")."
        };
        var actions = new List<ErrorActionDto>();
        if (NormalizeExternalUrl(config.StoreUrl) is { } storeUrl)
            actions.Add(new ErrorActionDto("STORE", "Lisans Satın Al", storeUrl, true));
        if (NormalizeExternalUrl(config.SupportUrl) is { } supportUrl)
            actions.Add(new ErrorActionDto("SUPPORT", "Destek", supportUrl, actions.Count == 0));
        return new ErrorDto(false, code, "Raven Map lisansı doğrulanamadı", message,
            "İnternet bağlantınızı ve lisans anahtarınızı kontrol edin. Cihaz değiştiyse owner panelinden Raven Map kurulum bağını sıfırlayın.",
            "error", actions);
    }

    private static string NormalizeLicenseProduct(string? value)
    {
        var product = (value ?? "").Trim().ToLowerInvariant();
        if (product != "raven_map")
            throw new InvalidDataException("Raven Map istemcisinin LicenseProduct ayarı 'raven_map' olmalıdır.");
        return product;
    }

    private sealed record ActivateDto(bool Ok, string? ActivationToken, string? SteamId, DateTimeOffset? ExpiresUtc, DateTimeOffset? GraceEndsUtc, string? Plan, string? NextPlan, DateTimeOffset? PlanChangesUtc, string? OfflineToken, string? ServerInstanceId);
    private sealed record ValidateDto(bool Ok, string? SteamId, DateTimeOffset? ExpiresUtc, DateTimeOffset? GraceEndsUtc, string? Plan, string? NextPlan, DateTimeOffset? PlanChangesUtc, string? OfflineToken, string? ServerInstanceId);
    private sealed record LicenseCenterDto(bool Ok, string? Token, string? LicenseId, string? Product, List<string>? Products, DateTimeOffset? ExpiresUtc, DateTimeOffset? GraceUntilUtc, string? Plan, string? Customer);
    private sealed record LicenseCenterErrorDto(bool Ok = false, string Code = "");
    private sealed record ErrorActionDto(string Id = "", string Label = "", string Url = "", bool Primary = false);
    private sealed record ErrorDto(bool Ok = false, string Code = "", string Title = "", string Message = "", string Detail = "", string Severity = "error", List<ErrorActionDto>? Actions = null)
    {
        public UserIssue ToIssue()
        {
            var title = string.IsNullOrWhiteSpace(Title) ? "Raven işlemi tamamlanamadı" : Title;
            var message = string.IsNullOrWhiteSpace(Message) ? "Raven isteği tamamlanamadı." : Message;
            var detail = string.IsNullOrWhiteSpace(Detail) ? "İşlemi yeniden deneyin." : Detail;
            var actions = (Actions ?? [])
                .Where(a => !string.IsNullOrWhiteSpace(a.Label) && Uri.TryCreate(a.Url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
                .Select(a => new UserAction(a.Id ?? "", a.Label, a.Url, a.Primary))
                .ToList();
            return new UserIssue(Code ?? "", title, message, detail, string.IsNullOrWhiteSpace(Severity) ? "error" : Severity, actions);
        }
    }
    private sealed record PrefabCatalogEnvelopeDto(string? Payload,string? Signature);
    private sealed record PrefabCatalogDocumentDto(int SchemaVersion,string? Plan,DateTimeOffset GeneratedUtc,List<PrefabCatalogVariantDto>? Variants);
    private sealed record PrefabCatalogVariantDto(string? MonumentId,string? Id,string? Name,string? MinimumPlan,List<PrefabCatalogTargetDto>? Targets);
    private sealed record PrefabCatalogTargetDto(string? ContentId,string? TargetFileName,string? Sha256,long Size);
}

public sealed record LicenseCacheSnapshot(string ActivationToken, string SteamId, DateTimeOffset? ExpiresUtc, string Plan);

internal static class OfflineLicenseVerifier
{
    private static string PublicKeyPem => SecurityKeys.LicensePublicKeyPem;
    public static bool TryValidate(string token, string deviceHash, out string steamId, out DateTimeOffset validUntil, out string plan)
    {
        steamId = ""; validUntil = default; plan = EntitlementPlan.Standard;
        try
        {
            var parts = token.Split('.'); if (parts.Length != 2) return false;
            var payload = Decode(parts[0]); var signature = Decode(parts[1]);
            using var rsa = RSA.Create(); rsa.ImportFromPem(PublicKeyPem);
            if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return false;
            var doc = JsonDocument.Parse(payload); var root = doc.RootElement;
            var storedDevice = root.GetProperty("DeviceHash").GetString() ?? "";
            if (!storedDevice.Equals(deviceHash, StringComparison.OrdinalIgnoreCase)) return false;
            steamId = root.GetProperty("SteamId").GetString() ?? "";
            if (root.TryGetProperty("Plan", out var planElement)) plan = EntitlementPlan.Normalize(planElement.GetString());
            var exp = root.GetProperty("Exp").GetInt64(); validUntil = DateTimeOffset.FromUnixTimeSeconds(exp);
            return validUntil > DateTimeOffset.UtcNow;
        }
        catch { return false; }
    }
    private static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s += new string('=', (4 - s.Length % 4) % 4);
        return Convert.FromBase64String(s);
    }
}
