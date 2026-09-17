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

static class LicenseRules
{
    public static (string Code, string Message, int Status)? CheckForActivation(LicenseRecord l, string steamId, string deviceHash, string? legacyDeviceHash, TimeSpan gracePeriod)
    {
        if (l.Status == "REVOKED") return ("LICENSE_REVOKED", "Bu lisans iptal edilmiştir.", 403);
        if (l.Status == "SUSPENDED") return ("LICENSE_SUSPENDED", "Bu lisans geçici olarak askıya alınmıştır.", 403);
        if (l.Status == "EXPIRED") return ("PURCHASE_REQUIRED", "Aboneliğiniz sona ermiştir. Devam etmek için aboneliğinizi yenileyin.", 402);
        if (IsPastGrace(l, gracePeriod, DateTimeOffset.UtcNow)) return ("PURCHASE_REQUIRED", "Aboneliğinizin süresi ve ek kullanım süresi sona ermiştir.", 402);
        if (!string.IsNullOrWhiteSpace(l.SteamId) && !l.SteamId.Equals(steamId, StringComparison.Ordinal)) return ("STEAM_MISMATCH", "Bu lisans başka bir Steam hesabına bağlıdır.", 409);
        if (!string.IsNullOrWhiteSpace(l.DeviceHash) && !DeviceMatches(l.DeviceHash, deviceHash, legacyDeviceHash)) return ("DEVICE_MISMATCH", "Bu lisans başka bir bilgisayarda aktiftir.", 409);
        return null;
    }
    public static (string Code, string Message, int Status)? CheckForValidation(LicenseRecord l, string deviceHash, string? legacyDeviceHash, TimeSpan gracePeriod)
    {
        if (l.Status == "REVOKED") return ("LICENSE_REVOKED", "Bu lisans iptal edilmiştir.", 403);
        if (l.Status == "SUSPENDED") return ("LICENSE_SUSPENDED", "Bu lisans askıya alınmıştır.", 403);
        if (l.Status == "EXPIRED") return ("PURCHASE_REQUIRED", "Aboneliğiniz sona ermiştir. Devam etmek için aboneliğinizi yenileyin.", 402);
        if (IsPastGrace(l, gracePeriod, DateTimeOffset.UtcNow)) return ("PURCHASE_REQUIRED", "Aboneliğinizin süresi ve ek kullanım süresi sona ermiştir.", 402);
        if (string.IsNullOrWhiteSpace(l.DeviceHash) || !DeviceMatches(l.DeviceHash, deviceHash, legacyDeviceHash)) return ("DEVICE_MISMATCH", "Cihaz doğrulaması başarısız.", 409);
        return null;
    }
    public static bool IsLegacyDeviceMatch(LicenseRecord l, string deviceHash, string? legacyDeviceHash) =>
        !string.IsNullOrWhiteSpace(l.DeviceHash) &&
        !l.DeviceHash.Equals(deviceHash, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(legacyDeviceHash) &&
        l.DeviceHash.Equals(legacyDeviceHash, StringComparison.OrdinalIgnoreCase);

    private static bool DeviceMatches(string stored, string current, string? legacy) =>
        stored.Equals(current, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(legacy) && stored.Equals(legacy, StringComparison.OrdinalIgnoreCase));
    public static DateTimeOffset GetOfflineTokenExpiry(LicenseRecord l, TimeSpan gracePeriod, DateTimeOffset now)
    {
        var normal = now.AddHours(72);
        var result = l.ExpiresUtc is null ? normal : Min(normal, l.ExpiresUtc.Value.Add(gracePeriod));
        // Imzali offline token eski Premium yetkisini plan gecis tarihinin otesine tasiyamaz.
        if (!string.IsNullOrWhiteSpace(l.NextPlan) && l.PlanChangesUtc is not null)
            result = Min(result, l.PlanChangesUtc.Value);
        return result;
    }
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
    public static bool IsPastGrace(LicenseRecord l, TimeSpan gracePeriod, DateTimeOffset now)
        => l.ExpiresUtc is not null && now >= l.ExpiresUtc.Value.Add(gracePeriod);
}
