using System.Reflection;

namespace RavenMapPanel;

internal static class SecurityKeys
{
    private static readonly Lazy<string> LicenseKey = new(() => LoadPem("license_public.pem"));
    private static readonly Lazy<string> UpdateKey = new(() => LoadPem("update_public.pem"));

    public static string LicensePublicKeyPem => LicenseKey.Value;
    public static string UpdatePublicKeyPem => UpdateKey.Value;

    private static string LoadPem(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            throw new InvalidOperationException($"Raven güvenlik anahtarı embedded resource olarak bulunamadı: {fileName}");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Raven güvenlik anahtarı açılamadı: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
