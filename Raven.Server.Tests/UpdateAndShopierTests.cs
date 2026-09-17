using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

public sealed class UpdateAndShopierTests
{
    [Fact]
    public void ReleaseRecord_CanBeDeletedSoVersionBaselineCanBeReset()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RavenServerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var db = new RavenDb(Path.Combine(directory, "test.db"));
            db.Initialize();
            db.UpsertRelease(new ReleaseRecord("1.7.11", true, "1.7.10", "old.ravenpkg", "sha", "sig", "old", DateTimeOffset.UtcNow));

            Assert.NotNull(db.GetRelease("1.7.11"));
            Assert.True(db.DeleteRelease("1.7.11"));
            Assert.Null(db.GetRelease("1.7.11"));
            Assert.False(db.DeleteRelease("1.7.11"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SignedUpdatePackage_IsAccepted()
    {
        WithSignedPackage("2.0.0", (verifier, package, signature) =>
        {
            Assert.True(verifier.Verify(package, signature, "2.0.0", out var sha, out var error), error);
            Assert.Equal(64, sha.Length);
        });
    }

    [Fact]
    public void UpdatePackage_WithWrongSignature_IsRejected()
    {
        WithSignedPackage("2.0.0", (verifier, package, _) =>
        {
            var badSignature = Convert.ToBase64String(RandomNumberGenerator.GetBytes(256));
            Assert.False(verifier.Verify(package, badSignature, "2.0.0", out _, out var error));
            Assert.Contains("geçersiz", error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void UpdatePackage_WithDifferentManifestVersion_IsRejected()
    {
        WithSignedPackage("2.0.0", (verifier, package, signature) =>
        {
            Assert.False(verifier.Verify(package, signature, "2.1.0", out _, out var error));
            Assert.Contains("eşleşmiyor", error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ServerUpdatePackage_RsaSignatureDetectsTampering()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RavenServerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var package = Path.Combine(directory, "RavenServer_UPDATE_v1.7.0.zip");
            var signaturePath = package + ".sig";
            var hashPath = package + ".sha256";
            var publicKeyPath = Path.Combine(directory, "update_public.pem");
            File.WriteAllText(package, "signed-server-package");

            using var rsa = RSA.Create(2048);
            File.WriteAllText(publicKeyPath, rsa.ExportSubjectPublicKeyInfoPem());
            var hash = SHA256.HashData(File.ReadAllBytes(package));
            File.WriteAllText(hashPath, Convert.ToHexString(hash).ToLowerInvariant());
            File.WriteAllText(signaturePath, Convert.ToBase64String(rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));

            Assert.True(ServerUpdateSecurity.VerifyPackage(package, signaturePath, publicKeyPath, out var validError), validError);

            File.AppendAllText(package, "tampered");
            Assert.False(ServerUpdateSecurity.VerifyPackage(package, signaturePath, publicKeyPath, out var tamperError));
            Assert.Contains("SHA-256", tamperError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ShopierParser_MatchesConfiguredProductById()
    {
        using var doc = JsonDocument.Parse("""
{"lineItems":[{"productId":"STD-1","title":"Raven Standard","quantity":1},{"productId":"PREM-1","title":"Raven Premium","quantity":1}]}
""");

        var match = ShopierApiParser.FindConfiguredProduct(doc.RootElement, "PREM-1", "");

        Assert.NotNull(match);
        Assert.Equal("PREM-1", match.Value.ProductId);
        Assert.Equal("Raven Premium", match.Value.Title);
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("{\"orders\":[{\"id\":\"1\"},{\"id\":\"2\"}]}", 2)]
    [InlineData("{\"data\":[{\"id\":\"1\"}]}", 1)]
    public void ShopierParser_AcceptsSupportedResponseShapes(string json, int expectedCount)
    {
        Assert.Equal(expectedCount, ShopierApiParser.ExtractOrders(json).Count);
    }

    private static void WithSignedPackage(string manifestVersion, Action<UpdatePackageVerifier, string, string> assertion)
    {
        var directory = Path.Combine(Path.GetTempPath(), "RavenServerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var package = Path.Combine(directory, "update.ravenpkg");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                var manifest = zip.CreateEntry("manifest.json");
                using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8))
                    writer.Write(JsonSerializer.Serialize(new { product = "RavenMapPanel", version = manifestVersion }));
                var payload = zip.CreateEntry("payload/RavenMapPanel.exe");
                using var payloadWriter = new StreamWriter(payload.Open(), Encoding.UTF8);
                payloadWriter.Write("test payload");
            }

            using var rsa = RSA.Create(2048);
            var publicKey = Path.Combine(directory, "update_public.pem");
            File.WriteAllText(publicKey, rsa.ExportSubjectPublicKeyInfoPem());
            var hash = SHA256.HashData(File.ReadAllBytes(package));
            var signature = Convert.ToBase64String(rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            assertion(new UpdatePackageVerifier(publicKey), package, signature);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ShopierParser_MatchesConfiguredProductByExactTitleWhenIdIsBlank()
    {
        using var doc = JsonDocument.Parse("""{"lineItems":[{"productId":"12345","title":"Raven Map Panel Premium","type":"digital","quantity":1}]}""");

        var match = ShopierApiParser.FindConfiguredProduct(doc.RootElement, "", "Raven Map Panel Premium");

        Assert.NotNull(match);
        Assert.Equal("12345", match.Value.ProductId);
        Assert.Equal("Raven Map Panel Premium", match.Value.Title);
    }
}
