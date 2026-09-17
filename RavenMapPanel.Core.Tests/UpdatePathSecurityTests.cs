using Xunit;

namespace RavenMapPanel;

public sealed class UpdatePathSecurityTests
{
    [Fact]
    public void NumericVersionStaysInsideDedicatedUpdateDirectory()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "RavenUpdatePathTests");

        var resolved = UpdateService.ResolveSafeUpdateDirectory(temporaryRoot, "1.7.2");

        var expectedRoot = Path.GetFullPath(Path.Combine(temporaryRoot, "RavenMapPanel", "Update"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.StartsWith(expectedRoot, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("Update", "1.7.2"), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("..\\..\\victim")]
    [InlineData("C:\\Users\\Public\\victim")]
    [InlineData("v1.7.2")]
    [InlineData("1.7.2-beta")]
    [InlineData("")]
    public void UnsafeOrNonNumericVersionIsRejected(string version)
    {
        Assert.Throws<InvalidDataException>(() =>
            UpdateService.ResolveSafeUpdateDirectory(Path.GetTempPath(), version));
    }
}
