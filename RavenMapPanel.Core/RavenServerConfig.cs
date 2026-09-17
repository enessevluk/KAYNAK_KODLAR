using System.Text.Json;

namespace RavenMapPanel;

public sealed class RavenServerConfig
{
    public const string ProductionBaseUrl = "https://api.ravenrusttr.com.tr";
    public const string ProductionLicenseCenterUrl = "https://license.ravenrusttr.com.tr";
    public string BaseUrl { get; set; } = ProductionBaseUrl;
    public string LicenseCenterUrl { get; set; } = ProductionLicenseCenterUrl;
    public string LicenseProduct { get; set; } = "raven_map";
    public string StoreUrl { get; set; } = "https://www.shopier.com/ravenrust";
    public string SupportUrl { get; set; } = "";
    public bool AllowInsecureHttpForTesting { get; set; }

    public static RavenServerConfig Load()
    {
        var path = Path.Combine(SettingsStore.ConfigRoot, "raven_server.json");
        RavenServerConfig config = new();
        try
        {
            if (File.Exists(path))
                config = JsonSerializer.Deserialize<RavenServerConfig>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("Raven server config boş/okunamaz durumda.");
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Raven server config okunamadı: {path}. Dosyayı düzeltin veya RAVEN.bat > 6 ILK KURULUM / VDS ADRESI ile yeniden oluşturun.", ex);
        }

        // VDS değişiminde EXE'yi yeniden derlemek zorunda kalmamak için en yüksek öncelikli override.
        var environmentUrl = Environment.GetEnvironmentVariable("RAVEN_SERVER_URL")?.Trim();
        if (!string.IsNullOrWhiteSpace(environmentUrl))
            config.BaseUrl = environmentUrl;

        var licenseEnvironmentUrl = Environment.GetEnvironmentVariable("RAVEN_LICENSE_CENTER_URL")?.Trim();
        if (!string.IsNullOrWhiteSpace(licenseEnvironmentUrl))
            config.LicenseCenterUrl = licenseEnvironmentUrl;

        try
        {
            if (!File.Exists(path))
                File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }

        return config;
    }

    public Uri GetBaseUri()
    {
        return GetSecureUri(BaseUrl, "Raven servis adresi");
    }

    public Uri GetLicenseCenterUri()
    {
        return GetSecureUri(LicenseCenterUrl, "Raven lisans merkezi adresi");
    }

    private Uri GetSecureUri(string? configuredValue, string label)
    {
        var value = (configuredValue ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{label} boş. Config\\raven_server.json dosyasını kontrol edin.");
        if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"{label} geçersiz: {value}");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{label} HTTP veya HTTPS olmalıdır.");

        // Production müşteri build'i bu alanı false üretir ve yalnız HTTPS kabul eder.
        // VDS üzerinde doğrudan IP:port ile geliştirme/test yapmak isteyen sahip ise
        // AllowInsecureHttpForTesting=true ile bilinçli şekilde HTTP kullanabilir.
        if (uri.Scheme != Uri.UriSchemeHttps && !AllowInsecureHttpForTesting)
            throw new InvalidOperationException(
                $"{label} HTTPS kullanmalıdır. Mevcut adres: {value}. " +
                "Yalnız yerel testte Config\\raven_server.json içinde AllowInsecureHttpForTesting=true kullanılabilir.");
        return uri;
    }
}
