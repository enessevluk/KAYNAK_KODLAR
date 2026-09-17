sealed class ServerRuntime
{
    public required RavenDb Db { get; init; }
    public required OfflineTokenSigner Signer { get; init; }
    public required PrefabContentService PrefabContent { get; init; }
    public required UpdatePackageVerifier UpdateVerifier { get; init; }
    public required SteamOpenIdService Steam { get; init; }
    public required ShopierOrderPoller ShopierPoller { get; init; }
    public required OwnerSettingsService OwnerSettings { get; init; }
    public required string PublicBaseUrl { get; init; }
    public required string AdminUser { get; init; }
    public required string AdminPassword { get; init; }
    public required string AdminTotpSecret { get; init; }
    public required string UpdateDirectory { get; init; }
    public required string ServerInstanceId { get; init; }
    public required string ServerVersion { get; init; }
    public required TimeSpan GracePeriod { get; init; }
    public required int ServerProtocolVersion { get; init; }
    public required bool IsDevelopment { get; init; }

    public IReadOnlyList<ShopierProductConfig> ShopierProducts { get; private set; } = Array.Empty<ShopierProductConfig>();
    public string ShopierBuyUrl { get; private set; } = "";
    public int ShopierCheckIntervalSeconds { get; private set; } = 60;
    public string SupportUrl { get; private set; } = "";
    public string SupportLabel { get; private set; } = "Discord Destek";
    public string SupportMessage { get; private set; } = "Sorununuz devam ederse Raven destek ekibine ulaşabilirsiniz.";
    public bool ShopierEnabled => ShopierPoller.Enabled;

    public void ApplyOwnerSettings(OwnerSettingsDocument document, ShopierRuntimeConfig? runtimeShopier = null)
    {
        var shopier = runtimeShopier ?? OwnerSettingsService.ToRuntimeShopier(document);
        ShopierProducts = shopier.Products;
        ShopierBuyUrl = shopier.BuyUrl;
        ShopierCheckIntervalSeconds = shopier.CheckIntervalSeconds;
        SupportUrl = (document.Support?.Url ?? "").Trim();
        SupportLabel = string.IsNullOrWhiteSpace(document.Support?.Label) ? "Discord Destek" : document.Support!.Label.Trim();
        SupportMessage = string.IsNullOrWhiteSpace(document.Support?.Message)
            ? "Sorununuz devam ederse Raven destek ekibine ulaşabilirsiniz."
            : document.Support!.Message.Trim();
        ShopierPoller.UpdateConfiguration(shopier);
    }
}
