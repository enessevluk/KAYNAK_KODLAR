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

sealed class UpdatePackageVerifier
{
    private readonly RSA rsa = RSA.Create();
    public UpdatePackageVerifier(string publicKeyPath) => rsa.ImportFromPem(File.ReadAllText(publicKeyPath));

    public bool Verify(string packagePath, string signatureText, string expectedVersion, out string sha256, out string error)
    {
        sha256 = "";
        error = "";
        try
        {
            byte[] hash;
            using (var packageStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan))
                hash = SHA256.HashData(packageStream);
            sha256 = Convert.ToHexString(hash).ToLowerInvariant();
            byte[] signature;
            try { signature = Convert.FromBase64String(signatureText.Trim()); }
            catch { error = "İmza Base64 formatında değil."; return false; }
            if (!rsa.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                error = "RSA dijital imzası geçersiz.";
                return false;
            }
            using var zip = System.IO.Compression.ZipFile.OpenRead(packagePath);
            var manifest = zip.GetEntry("manifest.json");
            if (manifest is null) { error = "manifest.json bulunamadı."; return false; }
            using var stream = manifest.Open();
            using var doc = JsonDocument.Parse(stream);
            var product = doc.RootElement.TryGetProperty("product", out var prod) ? prod.GetString() : null;
            var version = doc.RootElement.TryGetProperty("version", out var ver) ? ver.GetString() : null;
            if (!string.Equals(product, "RavenMapPanel", StringComparison.Ordinal))
            { error = "Paket ürünü RavenMapPanel değil."; return false; }
            if (!string.Equals(version, expectedVersion, StringComparison.OrdinalIgnoreCase))
            { error = $"Manifest sürümü ({version}) panelde girilen sürümle ({expectedVersion}) eşleşmiyor."; return false; }
            if (zip.GetEntry("payload/") is null && !zip.Entries.Any(x => x.FullName.StartsWith("payload/", StringComparison.OrdinalIgnoreCase)))
            { error = "payload klasörü bulunamadı."; return false; }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

