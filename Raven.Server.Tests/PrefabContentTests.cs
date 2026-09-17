using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

public sealed class PrefabContentTests
{
    [Fact]
    public void Catalog_OnlyIncludesContentAllowedByPlan()
    {
        var root=Path.Combine(Path.GetTempPath(),"RavenPrefabTests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"STANDARD","outpost"));
        Directory.CreateDirectory(Path.Combine(root,"PREMIUM","outpost"));
        var keyPath=Path.Combine(root,"license.pem");
        using var rsa=RSA.Create(2048);
        File.WriteAllText(keyPath,rsa.ExportPkcs8PrivateKeyPem());
        File.WriteAllBytes(Path.Combine(root,"STANDARD","outpost","outpost_standard.prefab.map"),[1,2,3]);
        File.WriteAllBytes(Path.Combine(root,"PREMIUM","outpost","outpost_premium.prefab.map"),[4,5,6]);
        try
        {
            var service=new PrefabContentService(root,new OfflineTokenSigner(keyPath));
            var standard=Read(service.CreateSignedCatalog(EntitlementPlans.Standard));
            var premium=Read(service.CreateSignedCatalog(EntitlementPlans.Premium));
            Assert.Single(standard.GetProperty("Variants").EnumerateArray());
            Assert.Equal(2,premium.GetProperty("Variants").GetArrayLength());
            Assert.DoesNotContain("outpost_premium",standard.GetRawText(),StringComparison.OrdinalIgnoreCase);
            Assert.Contains("outpost_premium",premium.GetRawText(),StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Standard Outpost",standard.GetRawText(),StringComparison.Ordinal);
            Assert.Contains("Standard Outpost",premium.GetRawText(),StringComparison.Ordinal);
            Assert.Contains("Premium Outpost",premium.GetRawText(),StringComparison.Ordinal);
            Assert.DoesNotContain("Premium Outpost",standard.GetRawText(),StringComparison.Ordinal);
        }
        finally { Directory.Delete(root,true); }
    }

    private static JsonElement Read(PrefabCatalogEnvelope envelope)
    {
        using var doc=JsonDocument.Parse(Convert.FromBase64String(envelope.Payload));
        return doc.RootElement.Clone();
    }
}
