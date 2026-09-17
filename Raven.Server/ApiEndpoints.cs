using System.Security.Cryptography;
using static ServerHelpers;

static class ApiEndpoints
{
    public static void Map(WebApplication app, ServerRuntime r)
    {
        var api = app.MapGroup("/api").RequireRateLimiting("api");

        api.MapGet("/server/info", () =>
        {
            var stats = r.Db.GetStats();
            return Results.Ok(new ServerInfoResponse(r.ServerInstanceId, r.ServerVersion, r.ServerProtocolVersion, stats.Total, stats.Active, r.Db.GetSchemaVersion()));
        });

        api.MapGet("/bootstrap", () =>
        {
            var settings=r.Db.GetOperationalSettings();
            var minimum=r.Db.GetLatestRelease()?.MinimumVersion??"";
            var now=DateTimeOffset.UtcNow;
            var announcementActive=!string.IsNullOrWhiteSpace(settings.AnnouncementId)
                && (settings.AnnouncementStartsUtc is null || settings.AnnouncementStartsUtc<=now)
                && (settings.AnnouncementEndsUtc is null || settings.AnnouncementEndsUtc>now);
            return Results.Ok(new BootstrapResponse(3,settings.MaintenanceEnabled,settings.MaintenanceMessage,
                announcementActive?settings.AnnouncementId:"",
                announcementActive?settings.AnnouncementTitle:"",
                announcementActive?settings.AnnouncementMessage:"",
                settings.AnnouncementLevel,announcementActive?settings.AnnouncementImageUrl:"",
                announcementActive?settings.AnnouncementButtonText:"",announcementActive?settings.AnnouncementButtonUrl:"",
                announcementActive?settings.AnnouncementStartsUtc:null,announcementActive?settings.AnnouncementEndsUtc:null,
                minimum,now,settings.HomeMediaUrls));
        });

        api.MapPost("/auth/session", (HttpContext ctx) =>
        {
            var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            r.Db.CreateAuthSession(sessionId, DateTimeOffset.UtcNow.AddMinutes(10));
            var externalBase = GetExternalBaseUrl(ctx, r);
            return Results.Ok(new { sessionId, loginUrl = $"{externalBase}/auth/steam?session={sessionId}" });
        });

        api.MapGet("/auth/session/{id}", (string id) =>
        {
            var session = r.Db.GetAuthSession(id);
            if (session is null) return Results.NotFound(new { code = "SESSION_NOT_FOUND" });
            if (session.ExpiresUtc < DateTimeOffset.UtcNow) return Results.Ok(new { status = "expired" });
            if (session.UsedUtc is not null) return Results.Ok(new { status = "used" });
            return Results.Ok(new { status = session.SteamId is null ? "pending" : "verified", steamId = session.SteamId });
        });

        api.MapGet("/shopier/status", () => Results.Ok(new
        {
            enabled = r.ShopierEnabled,
            lastSuccessfulPollUtc = r.ShopierPoller.LastSuccessfulPollUtc,
            lastError = r.ShopierPoller.LastError,
            nextCheckUtc = r.ShopierPoller.NextCheckUtc
        }));

        api.MapGet("/store/info", () => Results.Ok(new StoreInfoResponse(
            r.ShopierEnabled,
            r.ShopierBuyUrl,
            "Raven Map Panel",
            "Shopier sipariş notuna SteamID64 değerinizi eksiksiz yazın.",
            r.SupportUrl,
            r.SupportLabel
        )));

        api.MapPost("/access/activate", (AccessActivateRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.AuthSessionId) || string.IsNullOrWhiteSpace(req.DeviceHash))
                return Results.BadRequest(ApiError(r, "INVALID_REQUEST", message: "Steam oturumu ve cihaz bilgisi gereklidir."));
            var auth = r.Db.GetAuthSession(req.AuthSessionId);
            if (auth is null || auth.ExpiresUtc < DateTimeOffset.UtcNow || auth.UsedUtc is not null || string.IsNullOrWhiteSpace(auth.SteamId))
                return Results.BadRequest(ApiError(r, "STEAM_REQUIRED", message: "Steam hesabı doğrulanmadı."));

            var license = r.Db.FindLatestLicenseBySteamId(auth.SteamId!);
            if (license is null)
                return Results.Json(ApiError(r, "PURCHASE_REQUIRED", message: "Bu Steam hesabına ait aktif Raven Map Panel satın alımı bulunamadı."), statusCode: StatusCodes.Status402PaymentRequired);

            r.Db.ExpireIfPastGrace(license.Id, r.GracePeriod, DateTimeOffset.UtcNow);
            license = r.Db.GetLicense(license.Id)!;
            if (license.Status == "EXPIRED")
                return Results.Json(ApiError(r, "PURCHASE_REQUIRED", license, "Raven Map Panel aboneliğinizin süresi ve ek kullanım süresi sona erdi. Devam etmek için aboneliğinizi yenileyin."), statusCode: StatusCodes.Status402PaymentRequired);

            var state = LicenseRules.CheckForActivation(license, auth.SteamId!, req.DeviceHash, req.LegacyDeviceHash, r.GracePeriod);
            if (state is not null) return Results.Json(ApiError(r, state.Value.Code, license, state.Value.Message), statusCode: state.Value.Status);
            if (!r.Db.TryConsumeAuthSession(req.AuthSessionId, DateTimeOffset.UtcNow))
                return Results.BadRequest(ApiError(r, "SESSION_USED", message: "Steam oturumu daha önce kullanılmış veya süresi dolmuş."));
            var rawActivationToken = Base64Url(RandomNumberGenerator.GetBytes(32));
            var activated = r.Db.TryActivateLicense(license.Id, auth.SteamId!, req.DeviceHash, req.LegacyDeviceHash, Hash(rawActivationToken), req.ClientVersion ?? "", DateTimeOffset.UtcNow);
            if (!activated)
            {
                var current = r.Db.GetLicense(license.Id);
                var concurrentState = current is null ? null : LicenseRules.CheckForActivation(current, auth.SteamId!, req.DeviceHash, req.LegacyDeviceHash, r.GracePeriod);
                var code = concurrentState?.Code ?? "ACTIVATION_CONFLICT";
                var msg = concurrentState?.Message ?? "Lisans aynı anda başka bir aktivasyon isteği tarafından güncellendi. Steam doğrulamasını yeniden yapın.";
                var status = concurrentState?.Status ?? StatusCodes.Status409Conflict;
                return Results.Json(ApiError(r, code, current, msg), statusCode: status);
            }
            var updated = r.Db.GetLicense(license.Id)!;
            var offlineUntil = LicenseRules.GetOfflineTokenExpiry(updated, r.GracePeriod, DateTimeOffset.UtcNow);
            var offlineToken = r.Signer.Create(updated, req.DeviceHash, offlineUntil);
            var graceEndsUtc = updated.ExpiresUtc?.Add(r.GracePeriod);
            return Results.Ok(new ActivateResponse(true, rawActivationToken, auth.SteamId!, updated.ExpiresUtc, graceEndsUtc, updated.Plan, updated.NextPlan, updated.PlanChangesUtc, offlineToken, r.ServerInstanceId));
        });

        api.MapPost("/license/validate", (ValidateRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.ActivationToken) || string.IsNullOrWhiteSpace(req.DeviceHash))
                return Results.BadRequest(ApiError(r, "INVALID_REQUEST", message: "Aktivasyon bilgisi eksik."));
            var license = r.Db.FindLicenseByActivationToken(req.ActivationToken);
            if (license is null) return Results.Json(ApiError(r, "ACTIVATION_INVALID"), statusCode: StatusCodes.Status401Unauthorized);
            r.Db.ExpireIfPastGrace(license.Id, r.GracePeriod, DateTimeOffset.UtcNow);
            license = r.Db.GetLicense(license.Id)!;
            if (license.Status == "EXPIRED")
                return Results.Json(ApiError(r, "PURCHASE_REQUIRED", license, "Raven Map Panel aboneliğinizin süresi ve ek kullanım süresi sona erdi. Devam etmek için aboneliğinizi yenileyin."), statusCode: StatusCodes.Status402PaymentRequired);
            var state = LicenseRules.CheckForValidation(license, req.DeviceHash, req.LegacyDeviceHash, r.GracePeriod);
            if (state is not null) return Results.Json(ApiError(r, state.Value.Code, license, state.Value.Message), statusCode: state.Value.Status);
            if (LicenseRules.IsLegacyDeviceMatch(license, req.DeviceHash, req.LegacyDeviceHash) && !string.IsNullOrWhiteSpace(license.DeviceHash))
                r.Db.TryUpdateDeviceHash(license.Id, license.DeviceHash!, req.DeviceHash);
            r.Db.TouchLicense(license.Id, req.ClientVersion ?? "", DateTimeOffset.UtcNow);
            var updated = r.Db.GetLicense(license.Id)!;
            var offlineUntil = LicenseRules.GetOfflineTokenExpiry(updated, r.GracePeriod, DateTimeOffset.UtcNow);
            var offlineToken = r.Signer.Create(updated, req.DeviceHash, offlineUntil);
            var graceEndsUtc = updated.ExpiresUtc?.Add(r.GracePeriod);
            return Results.Ok(new ValidateResponse(true, updated.SteamId!, updated.ExpiresUtc, graceEndsUtc, updated.Plan, updated.NextPlan, updated.PlanChangesUtc, offlineToken, r.ServerInstanceId));
        });

        api.MapGet("/prefabs/catalog", (HttpContext ctx) =>
        {
            var license = FindActiveLicense(ctx,r);
            if (license is null) return Results.Unauthorized();
            return Results.Ok(r.PrefabContent.CreateSignedCatalog(license.Plan));
        });

        api.MapGet("/prefabs/file/{contentId}", (HttpContext ctx,string contentId) =>
        {
            var license = FindActiveLicense(ctx,r);
            if (license is null) return Results.Unauthorized();
            if (!r.PrefabContent.TryResolve(contentId,license.Plan,out var path,out var fileName))
                return Results.NotFound();
            return Results.File(path,"application/octet-stream",fileName,enableRangeProcessing:false);
        });

        api.MapGet("/update/check", (string currentVersion) =>
        {
            var latest = r.Db.GetLatestRelease();
            if (latest is null) return Results.Ok(new UpdateCheckResponse(false, false, currentVersion, currentVersion, "", "", ""));
            var needs = CompareVersions(currentVersion, latest.Version) < 0;
            var forcedByMinimum = !string.IsNullOrWhiteSpace(latest.MinimumVersion) && CompareVersions(currentVersion, latest.MinimumVersion) < 0;
            var mandatory = needs && (latest.Mandatory || forcedByMinimum);
            return Results.Ok(new UpdateCheckResponse(needs, mandatory, currentVersion, latest.Version, latest.Notes, latest.Sha256, latest.Signature));
        });

        api.MapGet("/update/download/{version}", (HttpContext ctx, string version) =>
        {
            var license = FindActiveLicense(ctx, r);
            if (license is null) return Results.Unauthorized();
            var release = r.Db.GetRelease(version);
            if (release is null) return Results.NotFound();
            var path = Path.Combine(r.UpdateDirectory, release.FileName);
            if (!File.Exists(path)) return Results.NotFound();
            return Results.File(path, "application/octet-stream", release.FileName, enableRangeProcessing: true);
        });
    }

    private static LicenseRecord? FindActiveLicense(HttpContext ctx,ServerRuntime r)
    {
        var token=ctx.Request.Headers["X-Raven-Activation"].FirstOrDefault();
        var deviceHash=ctx.Request.Headers["X-Raven-Device"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(deviceHash)) return null;
        var license=r.Db.FindLicenseByActivationToken(token);
        if (license is null) return null;
        r.Db.ExpireIfPastGrace(license.Id,r.GracePeriod,DateTimeOffset.UtcNow);
        license=r.Db.GetLicense(license.Id);
        if (license is null || LicenseRules.CheckForValidation(license,deviceHash,null,r.GracePeriod) is not null)
            return null;
        r.Db.TouchLicense(license.Id, "content", DateTimeOffset.UtcNow);
        return license;
    }
}
