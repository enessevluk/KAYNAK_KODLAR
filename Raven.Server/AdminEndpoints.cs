using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using static ServerHelpers;

static class AdminEndpoints
{
    public static void Map(WebApplication app, ServerRuntime r)
    {
        var admin = app.MapGroup("/admin");

        admin.MapGet("/login", (HttpContext ctx, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
            Results.Content(AdminPages.Login(antiforgery.GetAndStoreTokens(ctx).RequestToken ?? "", !string.IsNullOrWhiteSpace(r.AdminTotpSecret)), "text/html", Encoding.UTF8));
        admin.MapPost("/login", async (HttpContext ctx, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var user = form["username"].ToString();
            var pass = form["password"].ToString();
            var totpCode = form["totpCode"].ToString();
            var rememberMe = form["rememberMe"].ToString() == "1";
            if (!user.Equals(r.AdminUser, StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(pass)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(r.AdminPassword))) ||
                !AdminTotp.Verify(r.AdminTotpSecret,totpCode,DateTimeOffset.UtcNow))
            {
                r.Db.RecordAdminAudit(user, "LOGIN_FAILED", "admin", "Kullanıcı adı veya parola yanlış.", RemoteIp(ctx));
                return Results.Content(AdminPages.Login(antiforgery.GetAndStoreTokens(ctx).RequestToken ?? "", !string.IsNullOrWhiteSpace(r.AdminTotpSecret), "Kullanıcı adı, parola veya doğrulama kodu yanlış."), "text/html", Encoding.UTF8, statusCode: 401);
            }
            var identity = new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, user) },
                CookieAuthenticationDefaults.AuthenticationScheme);
            var authProperties = new AuthenticationProperties
            {
                IsPersistent = rememberMe,
                AllowRefresh = true
            };
            if (rememberMe) authProperties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new System.Security.Claims.ClaimsPrincipal(identity), authProperties);
            r.Db.RecordAdminAudit(user, "LOGIN_SUCCESS", "admin", rememberMe ? "Admin oturumu açıldı (30 gün hatırla)." : "Admin oturumu açıldı.", RemoteIp(ctx));
            return Results.Redirect("/admin");
        }).RequireRateLimiting("admin-login");

        admin.MapPost("/logout", async (HttpContext ctx) =>
        {
            Audit(r, ctx, "LOGOUT", "admin", "Admin oturumu kapatıldı.");
            await ctx.SignOutAsync();
            return Results.Redirect("/admin/login");
        }).RequireAuthorization();

        admin.MapGet("", (HttpContext ctx, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) => Results.Content(AdminPages.Dashboard(
            r.Db.GetStats(), r.Db.ListLicenses(100), r.Db.ListShopierOrders(100), r.Db.ListReleases(), r.Db.ListAdminAudit(100), r.Db.GetOperationalSettings(),
            r.ShopierEnabled, r.ShopierBuyUrl, r.ShopierProducts, r.ShopierCheckIntervalSeconds,
            r.ShopierPoller.LastSuccessfulPollUtc, r.ShopierPoller.LastError, r.ShopierPoller.InitialSyncCompleted,
            r.ShopierPoller.BaselineUtc, r.ShopierPoller.CursorUtc, r.ServerInstanceId, r.ServerVersion, r.Db.GetSchemaVersion(), r.IsDevelopment, r.OwnerSettings.Load(),
            antiforgery.GetAndStoreTokens(ctx).RequestToken ?? ""),
            "text/html", Encoding.UTF8)).RequireAuthorization();

        admin.MapPost("/operations/update", async (HttpContext ctx) =>
        {
            var form=await ctx.Request.ReadFormAsync();
            var maintenance=form["maintenanceEnabled"].ToString()=="1";
            var maintenanceMessage=form["maintenanceMessage"].ToString();
            var announcementTitle=form["announcementTitle"].ToString();
            var announcementMessage=form["announcementMessage"].ToString();
            var announcementLevel=form["announcementLevel"].ToString();
            if (!TryNormalizeMediaUrls(form["homeMediaUrls"].ToString(), "Ana sayfa görselleri", out var homeMediaUrls, out var homeMediaError))
                return Results.BadRequest(homeMediaError);
            if (!TryNormalizeMediaUrls(form["announcementImageUrl"].ToString(), "Duyuru görselleri", out var announcementImageUrl, out var imageError))
                return Results.BadRequest(imageError);
            const string announcementButtonText = "";
            const string announcementButtonUrl = "";
            DateTimeOffset? LocalUtc(string value) => DateTimeOffset.TryParse(value,out var parsed)?parsed.ToUniversalTime():null;
            var starts=LocalUtc(form["announcementStarts"].ToString());
            var ends=LocalUtc(form["announcementEnds"].ToString());
            if (starts is not null && ends is not null && ends<=starts)
                return Results.BadRequest("Duyuru bitiş zamanı başlangıçtan sonra olmalıdır.");
            r.Db.SetOperationalSettings(maintenance,maintenanceMessage,announcementTitle,announcementMessage,
                announcementLevel,announcementImageUrl,announcementButtonText,announcementButtonUrl,starts,ends,homeMediaUrls);
            Audit(r,ctx,"OPERATIONS_UPDATE","bootstrap",$"Maintenance={maintenance}; AnnouncementTitle={announcementTitle}");
            return Results.Redirect("/admin#operations");
        }).RequireAuthorization();

        admin.MapPost("/announcement/delete", (HttpContext ctx) =>
        {
            var current=r.Db.GetOperationalSettings();
            r.Db.SetOperationalSettings(current.MaintenanceEnabled,current.MaintenanceMessage,"","","INFO","","","",null,null,current.HomeMediaUrls);
            Audit(r,ctx,"ANNOUNCEMENT_DELETE","bootstrap","Aktif duyuru kaldırıldı.");
            return Results.Redirect("/admin#operations");
        }).RequireAuthorization();

        admin.MapPost("/licenses/create", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var steamId = form["steamId"].ToString().Trim();
            var days = int.TryParse(form["days"], out var d) ? d : 0;
            var note = form["note"].ToString().Trim();
            var plan = EntitlementPlans.Normalize(form["plan"].ToString());
            if (!Regex.IsMatch(steamId, @"^7656119\d{10}$"))
                return Results.BadRequest("Geçerli bir SteamID64 girin.");
            var expires = days <= 0 ? (DateTimeOffset?)null : DateTimeOffset.UtcNow.AddDays(days);
            r.Db.CreateManualEntitlement(steamId, expires, note, plan);
            Audit(r, ctx, "LICENSE_CREATE", steamId, $"Plan={plan}; Days={days}; Note={note}");
            return Results.Redirect("/admin#licenses");
        }).RequireAuthorization();

        admin.MapPost("/licenses/{id:long}/action", async (HttpContext ctx, long id) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var action = form["action"].ToString();
            var reason = form["reason"].ToString().Trim();
            switch (action)
            {
                case "suspend": r.Db.SetLicenseStatus(id, "SUSPENDED", reason); break;
                case "activate": r.Db.SetLicenseStatus(id, "ACTIVE"); break;
                case "revoke": r.Db.SetLicenseStatus(id, "REVOKED", reason); break;
                case "reset_device": r.Db.ResetDevice(id); break;
                case "reset_steam": r.Db.ResetSteam(id); break;
                case "add30": r.Db.AddDays(id, 30); break;
                case "plan_standard": r.Db.SetLicensePlan(id, EntitlementPlans.Standard); break;
                case "plan_premium": r.Db.SetLicensePlan(id, EntitlementPlans.Premium); break;
            }
            Audit(r, ctx, "LICENSE_ACTION", id.ToString(), string.IsNullOrWhiteSpace(reason) ? action : $"{action}; Reason={reason}");
            return Results.Redirect("/admin#licenses");
        }).RequireAuthorization();

        admin.MapPost("/licenses/{id:long}/delete", async (HttpContext ctx, long id) =>
        {
            var form=await ctx.Request.ReadFormAsync();
            if(!string.Equals(form["confirmation"].ToString(),"DELETE",StringComparison.Ordinal))
                return Results.BadRequest("Kalıcı silme onayı geçersiz.");
            var license=r.Db.GetLicense(id);
            if(license is null) return Results.NotFound("Lisans bulunamadı.");
            if(!string.Equals(license.Status,"REVOKED",StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Yalnız iptal edilmiş lisanslar kalıcı olarak silinebilir.");
            if(!r.Db.DeleteRevokedLicense(id))
                return Results.BadRequest("Lisans silinemedi.");
            Audit(r,ctx,"LICENSE_DELETE",id.ToString(),$"SteamId={license.SteamId??"—"}; Source={license.Source}; Plan={license.Plan}");
            return Results.Redirect("/admin#licenses");
        }).RequireAuthorization();

        admin.MapPost("/settings/business", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            int IntValue(string name, int fallback) => int.TryParse(form[name].ToString(), out var value) ? value : fallback;
            bool BoolValue(string name) => form[name].ToString() == "1";

            var update = new BusinessSettingsUpdate(
                BoolValue("shopierEnabled"),
                form["shopierApiKey"].ToString(),
                form["shopierBuyUrl"].ToString(),
                form["shopierApiUrl"].ToString(),
                IntValue("shopierCheckIntervalSeconds", 60),
                new ProductSettingsUpdate(
                    BoolValue("standardEnabled"), form["standardProductId"].ToString(), form["standardTitle"].ToString(),
                    form["standardPrice"].ToString(), "TRY", IntValue("standardDays", 0)),
                new ProductSettingsUpdate(
                    BoolValue("premiumEnabled"), form["premiumProductId"].ToString(), form["premiumTitle"].ToString(),
                    form["premiumPrice"].ToString(), "TRY", IntValue("premiumDays", 0)),
                form["supportUrl"].ToString(),
                form["supportLabel"].ToString(),
                form["supportMessage"].ToString());

            try
            {
                var saved = r.OwnerSettings.UpdateBusinessSettings(update);
                r.ApplyOwnerSettings(saved);
                Audit(r, ctx, "BUSINESS_SETTINGS_UPDATE", "owner-config",
                    $"ShopierEnabled={r.ShopierEnabled}; Standard={update.Standard.ProductId}; Premium={update.Premium.ProductId}; SupportConfigured={!string.IsNullOrWhiteSpace(update.SupportUrl)}");
                return Results.Redirect("/admin#settings");
            }
            catch (Exception ex)
            {
                return Results.BadRequest("Ayarlar kaydedilemedi:\n" + ex.Message);
            }
        }).RequireAuthorization();

        admin.MapPost("/shopier/check", async (HttpContext ctx, CancellationToken ct) =>
        {
            if (!r.ShopierEnabled) return Results.BadRequest("Shopier API entegrasyonu etkin değil.");
            await r.ShopierPoller.CheckNowAsync(ct);
            Audit(r, ctx, "SHOPIER_CHECK", "shopier", string.IsNullOrWhiteSpace(r.ShopierPoller.LastError) ? "Kontrol tamamlandı." : r.ShopierPoller.LastError);
            return Results.Redirect("/admin#orders");
        }).RequireAuthorization();

        admin.MapPost("/shopier/orders/{orderId}/retry", async (HttpContext ctx, string orderId, CancellationToken ct) =>
        {
            if (!r.ShopierEnabled) return Results.BadRequest("Shopier API entegrasyonu etkin değil.");
            var normalized = (orderId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(normalized)) return Results.BadRequest("Sipariş ID eksik.");
            var rejected = r.Db.GetShopierOrder(normalized);
            if (rejected is null || !rejected.Status.Equals("REJECTED", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Yalnız REJECTED durumundaki Shopier siparişleri yeniden işlenebilir.");
            if (!r.Db.MarkShopierOrderRetryPending(normalized))
                return Results.BadRequest("Sipariş yeniden işleme için hazırlanamadı.");
            // Sipariş normal 5 dakikalık overlap penceresinin dışında olsa da API'den tekrar görülebilsin.
            r.Db.RewindShopierCursorUtc(rejected.ReceivedUtc.AddMinutes(-10));
            await r.ShopierPoller.CheckNowAsync(ct);
            Audit(r, ctx, "SHOPIER_RETRY", normalized, string.IsNullOrWhiteSpace(r.ShopierPoller.LastError) ? "Sipariş yeniden kontrol edildi." : r.ShopierPoller.LastError);
            return Results.Redirect("/admin#orders");
        }).RequireAuthorization();

        if (r.IsDevelopment)
        {
            admin.MapPost("/shopier/test-order", async (HttpContext ctx) =>
            {
                var form = await ctx.Request.ReadFormAsync();
                var steamId = form["steamId"].ToString().Trim();
                var orderId = form["orderId"].ToString().Trim();
                var requestedPlan = EntitlementPlans.Normalize(form["plan"].ToString());
                if (string.IsNullOrWhiteSpace(orderId)) orderId = "TEST-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!Regex.IsMatch(steamId, @"^7656119\d{10}$")) return Results.BadRequest("Geçerli bir SteamID64 girin.");
                var testProduct = r.ShopierProducts.FirstOrDefault(x => x.Plan.Equals(requestedPlan, StringComparison.OrdinalIgnoreCase))
                    ?? new ShopierProductConfig(true, "TEST-" + requestedPlan, "Raven Map Panel " + requestedPlan, "", "TRY", 0, requestedPlan);
                var testOrder = new ParsedShopierOrder(
                    orderId,
                    steamId,
                    string.IsNullOrWhiteSpace(testProduct.ProductId) ? "TEST-PRODUCT" : testProduct.ProductId,
                    string.IsNullOrWhiteSpace(testProduct.Title) ? "Raven Map Panel" : testProduct.Title,
                    "paid", steamId, DateTimeOffset.UtcNow);
                r.Db.ApplyShopierOrder(testOrder, testProduct.LicenseDays, r.GracePeriod, testProduct.Plan);
                Audit(r, ctx, "SHOPIER_TEST_ORDER", orderId, $"SteamId={steamId}; Plan={testProduct.Plan}");
                return Results.Redirect("/admin#orders");
            }).RequireAuthorization();
        }

        admin.MapPost("/releases/upload", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var version = form["version"].ToString().Trim();
            var minimum = form["minimumVersion"].ToString().Trim();
            var mandatory = form["mandatory"].ToString()=="1";
            var notes = form["notes"].ToString().Trim();
            var signature = form["signature"].ToString().Trim();
            var signatureFile = form.Files.GetFile("signatureFile");
            if (string.IsNullOrWhiteSpace(signature) && signatureFile is not null)
            {
                using var sr = new StreamReader(signatureFile.OpenReadStream());
                signature = (await sr.ReadToEndAsync()).Trim();
            }
            var file = form.Files.GetFile("package");
            if (file is null || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(signature))
                return Results.BadRequest("version, imza ve .ravenpkg gereklidir.");
            if (!Version.TryParse(NormalizeVersion(version), out _))
                return Results.BadRequest("Geçersiz sürüm numarası.");
            if (!string.IsNullOrWhiteSpace(minimum) && !Version.TryParse(NormalizeVersion(minimum), out _))
                return Results.BadRequest("Geçersiz minimum sürüm numarası.");

            var safeName = $"RavenMapPanel_{version}.ravenpkg";
            var target = Path.Combine(r.UpdateDirectory, safeName);
            var temp = target + ".upload";
            try
            {
                await using (var fs = File.Create(temp)) await file.CopyToAsync(fs);
                if (!r.UpdateVerifier.Verify(temp, signature, version, out var sha, out var verifyError))
                    return Results.BadRequest("Güncelleme paketi reddedildi: " + verifyError);
                File.Move(temp, target, true);
                r.Db.UpsertRelease(new ReleaseRecord(version, mandatory, minimum, safeName, sha, signature, notes, DateTimeOffset.UtcNow));
                Audit(r, ctx, "RELEASE_UPLOAD", version, $"File={safeName}; Sha256={sha}; Minimum={minimum}");
                return Results.Redirect("/admin#updates");
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }).RequireAuthorization()
          .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(512L * 1024 * 1024))
          .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestFormLimitsAttribute { MultipartBodyLengthLimit = 512L * 1024 * 1024 });

        admin.MapPost("/releases/{version}/delete", (HttpContext ctx, string version) =>
        {
            var release = r.Db.GetRelease(version);
            if (release is null) return Results.NotFound("Silinecek client sürümü bulunamadı.");

            var updateRoot = Path.GetFullPath(r.UpdateDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var packagePath = Path.GetFullPath(Path.Combine(updateRoot, release.FileName));
            if (!packagePath.StartsWith(updateRoot, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Güncelleme paket yolu güvenli değil.");

            if (!r.Db.DeleteRelease(version))
                return Results.Conflict("Client sürümü başka bir işlem tarafından değiştirildi.");

            try { if (File.Exists(packagePath)) File.Delete(packagePath); }
            catch (Exception ex)
            {
                Audit(r, ctx, "RELEASE_DELETE_FILE_WARNING", version, $"Veritabanı kaydı silindi; paket dosyası silinemedi: {ex.Message}");
                return Results.Redirect("/admin#updates");
            }

            Audit(r, ctx, "RELEASE_DELETE", version, $"Client sürümü ve paket dosyası silindi: {release.FileName}");
            return Results.Redirect("/admin#updates");
        }).RequireAuthorization();
    }

    private static bool TryNormalizeMediaUrls(string raw, string fieldName, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        if (string.IsNullOrWhiteSpace(raw)) return true;

        var urls = new List<string>();
        foreach (var line in raw.Replace("\r", "", StringComparison.Ordinal)
                     .Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                error = $"{fieldName} alanındaki her değer geçerli bir HTTPS adresi olmalıdır. Geçersiz değer: {line}";
                return false;
            }
            if (uri.AbsolutePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                error = $"{fieldName} alanında GIF kullanılamaz. JPG veya PNG fotoğraf bağlantısı ekleyin.";
                return false;
            }
            if (!urls.Contains(uri.ToString(), StringComparer.OrdinalIgnoreCase))
                urls.Add(uri.ToString());
        }

        if (urls.Count > 12)
        {
            error = $"{fieldName} alanına en fazla 12 adres ekleyebilirsiniz.";
            return false;
        }

        normalized = string.Join("\n", urls);
        return true;
    }

    private static void Audit(ServerRuntime runtime, HttpContext context, string action, string target, string detail) =>
        runtime.Db.RecordAdminAudit(context.User.Identity?.Name ?? "unknown", action, target, detail, RemoteIp(context));

    private static string RemoteIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
