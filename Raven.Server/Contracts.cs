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

record AccessActivateRequest(string AuthSessionId, string DeviceHash, string? LegacyDeviceHash, string? ClientVersion);
record StoreInfoResponse(bool Enabled, string BuyUrl, string ProductName, string PurchaseHint, string SupportUrl, string SupportLabel);
record UserActionResponse(string Id, string Label, string Url, bool Primary);
record ApiProblemResponse(bool Ok, string Code, string Title, string Message, string Detail, string Severity, IReadOnlyList<UserActionResponse> Actions);
record ServerInfoResponse(string InstanceId, string ServerVersion, int ProtocolVersion, int TotalEntitlements, int ActiveEntitlements, int DatabaseSchemaVersion);
record ActivateResponse(bool Ok, string ActivationToken, string SteamId, DateTimeOffset? ExpiresUtc, DateTimeOffset? GraceEndsUtc, string Plan, string? NextPlan, DateTimeOffset? PlanChangesUtc, string OfflineToken, string ServerInstanceId);
record ValidateRequest(string ActivationToken, string DeviceHash, string? LegacyDeviceHash, string? ClientVersion);
record ValidateResponse(bool Ok, string SteamId, DateTimeOffset? ExpiresUtc, DateTimeOffset? GraceEndsUtc, string Plan, string? NextPlan, DateTimeOffset? PlanChangesUtc, string OfflineToken, string ServerInstanceId);
record UpdateCheckResponse(bool UpdateAvailable, bool Mandatory, string CurrentVersion, string LatestVersion, string Notes, string Sha256, string Signature);
record ShopierProductConfig(bool Enabled, string ProductId, string Title, string ExpectedUnitPrice, string ExpectedCurrency, int LicenseDays, string Plan);
record AuthSession(string SessionId,string? SteamId,DateTimeOffset ExpiresUtc,DateTimeOffset? UsedUtc);
record LicenseRecord(long Id,string KeyLast4,string Status,string? SteamId,string? DeviceHash,DateTimeOffset? ExpiresUtc,DateTimeOffset CreatedUtc,DateTimeOffset? FirstActivationUtc,DateTimeOffset? LastSeenUtc,string LastClientVersion,string Note,string Source,string? ShopierOrderId,string? ShopierProductId,DateTimeOffset? PurchaseUtc,string Plan,string? NextPlan,DateTimeOffset? PlanChangesUtc,string StatusReason = "");
record ReleaseRecord(string Version,bool Mandatory,string MinimumVersion,string FileName,string Sha256,string Signature,string Notes,DateTimeOffset CreatedUtc);
record ShopierOrderRecord(string OrderId,string SteamId,string ProductId,string ProductTitle,string PaymentStatus,string OrderNote,string Status,string Error,long? LicenseId,DateTimeOffset ReceivedUtc);
record ParsedShopierOrder(string OrderId,string SteamId,string ProductId,string ProductTitle,string PaymentStatus,string Note,DateTimeOffset PurchaseUtc);
record AdminAuditRecord(long Id,DateTimeOffset CreatedUtc,string Actor,string Action,string Target,string Detail,string RemoteIp);
record OperationalSettings(bool MaintenanceEnabled,string MaintenanceMessage,string AnnouncementId,string AnnouncementTitle,string AnnouncementMessage,string AnnouncementLevel,string AnnouncementImageUrl,string AnnouncementButtonText,string AnnouncementButtonUrl,DateTimeOffset? AnnouncementStartsUtc,DateTimeOffset? AnnouncementEndsUtc,string HomeMediaUrls);
record BootstrapResponse(int SchemaVersion,bool Maintenance,string MaintenanceMessage,string AnnouncementId,string AnnouncementTitle,string AnnouncementMessage,string AnnouncementLevel,string AnnouncementImageUrl,string AnnouncementButtonText,string AnnouncementButtonUrl,DateTimeOffset? AnnouncementStartsUtc,DateTimeOffset? AnnouncementEndsUtc,string MinimumClientVersion,DateTimeOffset ServerTimeUtc,string HomeMediaUrls);
record PrefabCatalogEnvelope(string Payload,string Signature);
record PrefabCatalogDocument(int SchemaVersion,string Plan,DateTimeOffset GeneratedUtc,IReadOnlyList<PrefabCatalogVariant> Variants);
record PrefabCatalogVariant(string MonumentId,string Id,string Name,string MinimumPlan,IReadOnlyList<PrefabCatalogTarget> Targets);
record PrefabCatalogTarget(string ContentId,string TargetFileName,string Sha256,long Size);

static class EntitlementPlans
{
    public const string Standard = "STANDARD";
    public const string Premium = "PREMIUM";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Premium, StringComparison.OrdinalIgnoreCase) ? Premium : Standard;

    public static string ParseConfigured(string? value)
    {
        var plan = value?.Trim();
        if (string.Equals(plan, Standard, StringComparison.OrdinalIgnoreCase)) return Standard;
        if (string.Equals(plan, Premium, StringComparison.OrdinalIgnoreCase)) return Premium;
        throw new InvalidOperationException($"Shopier ürün Plan alanı yalnız STANDARD veya PREMIUM olabilir. Gelen değer: '{value ?? ""}'");
    }

}
