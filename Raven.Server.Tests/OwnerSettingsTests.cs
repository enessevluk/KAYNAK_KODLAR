using System.Text.Json;
using Xunit;

public sealed class OwnerSettingsTests
{
    [Fact]
    public void EnabledShopier_RejectsMissingOrShortApiKey()
    {
        var settings = CreateDocument(apiKey: "*", enabled: true);

        var errors = OwnerSettingsService.ValidateBusinessSettings(settings);

        Assert.Contains(errors, x => x.Contains("anahtarı", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BusinessUpdate_BlankReplacementKeepsExistingApiKey_AndPreservesRavenSection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RavenOwnerSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "raven_server.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(CreateDocument("abcdefghijklmnopqrstuvwxyz12345678", true), new JsonSerializerOptions { WriteIndented = true }));
            var service = new OwnerSettingsService(path);

            var saved = service.UpdateBusinessSettings(new BusinessSettingsUpdate(
                true,
                "",
                "https://www.shopier.com/ravenrust",
                "https://api.shopier.com/v1/orders",
                60,
                new ProductSettingsUpdate(true, "50153751", "Raven Map Panel Standard", "350.00", "TRY", 0),
                new ProductSettingsUpdate(true, "50153768", "Raven Map Panel Premium", "700.00", "TRY", 0),
                "https://discord.gg/raven",
                "Discord Destek",
                "Destek için Discord sunucumuza katılın."));

            Assert.Equal("abcdefghijklmnopqrstuvwxyz12345678", saved.Shopier!.ApiKey);
            Assert.Equal("https://discord.gg/raven", saved.Support!.Url);
            Assert.Equal("http://127.0.0.1:5088", saved.Raven!.PublicBaseUrl);
            Assert.True(File.Exists(path + ".backup"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DisabledShopier_DoesNotRequireApiKey()
    {
        var settings = CreateDocument(apiKey: "", enabled: false);

        var errors = OwnerSettingsService.ValidateBusinessSettings(settings);

        Assert.DoesNotContain(errors, x => x.Contains("anahtarı", StringComparison.OrdinalIgnoreCase));
    }

    private static OwnerSettingsDocument CreateDocument(string apiKey, bool enabled) => new()
    {
        Raven = new RavenOwnerSettings
        {
            PublicBaseUrl = "http://127.0.0.1:5088",
            ListenUrl = "http://0.0.0.0:5088",
            Environment = "Development"
        },
        Shopier = new ShopierOwnerSettings
        {
            Enabled = enabled,
            ApiKey = apiKey,
            BuyUrl = "https://www.shopier.com/ravenrust",
            ApiUrl = "https://api.shopier.com/v1/orders",
            Products =
            [
                new ShopierOwnerProductSettings
                {
                    Enabled = true,
                    ProductId = "50153751",
                    Title = "Raven Map Panel Standard",
                    ExpectedUnitPrice = "350.00",
                    ExpectedCurrency = "TRY",
                    Plan = EntitlementPlans.Standard
                }
            ]
        },
        Support = new SupportOwnerSettings()
    };
}
