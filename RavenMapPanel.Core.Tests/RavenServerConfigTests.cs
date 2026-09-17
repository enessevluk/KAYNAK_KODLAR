using Xunit;

namespace RavenMapPanel;

public sealed class RavenServerConfigTests
{
    [Fact]
    public void Defaults_TargetProductionHttps()
    {
        var config = new RavenServerConfig();

        Assert.Equal("https", config.GetBaseUri().Scheme);
        Assert.Equal("api.ravenrusttr.com.tr", config.GetBaseUri().Host);
        Assert.Equal("https", config.GetLicenseCenterUri().Scheme);
        Assert.Equal("license.ravenrusttr.com.tr", config.GetLicenseCenterUri().Host);
        Assert.Equal("raven_map", config.LicenseProduct);
        Assert.Equal("https://www.shopier.com/ravenrust", config.StoreUrl);
        Assert.False(config.AllowInsecureHttpForTesting);
    }

    [Fact]
    public void Http_RemainsExplicitlyTestOnly()
    {
        var config = new RavenServerConfig { BaseUrl = "http://127.0.0.1:5088" };

        Assert.Throws<InvalidOperationException>(() => config.GetBaseUri());

        config.AllowInsecureHttpForTesting = true;
        Assert.Equal("http", config.GetBaseUri().Scheme);

        config.LicenseCenterUrl = "http://127.0.0.1:8080";
        Assert.Equal("http", config.GetLicenseCenterUri().Scheme);
    }
}
