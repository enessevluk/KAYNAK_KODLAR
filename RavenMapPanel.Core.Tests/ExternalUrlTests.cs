using Xunit;

namespace RavenMapPanel;

public sealed class ExternalUrlTests
{
    [Fact]
    public void DiscordShortInvite_IsOpenedAsCanonicalWebInvite()
    {
        var result = LicenseService.NormalizeExternalUrl("https://discord.gg/jSUATtKhPG");

        Assert.Equal("https://discord.com/invite/jSUATtKhPG", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not-a-url")]
    public void UnsafeOrInvalidExternalAddress_IsRejected(string value)
    {
        Assert.Null(LicenseService.NormalizeExternalUrl(value));
    }
}
