using RavenMapPanel;
using Xunit;

namespace RavenMapPanel.Core.Tests;

public sealed class UpdateScriptTests
{
    [Fact]
    public void Script_ReplacesPayloadFilesWithoutRenamingInstallFolder_AndRelaunchesApp()
    {
        var script = UpdateService.BuildUpdateScript(
            @"C:\Temp\payload",
            @"C:\Raven\Client",
            1234,
            "1.0.1",
            "RavenMapPanel.exe",
            "testnonce");

        Assert.Contains("Yalnız imzalı payload dosyalarını yerinde değiştir", script, StringComparison.Ordinal);
        Assert.Contains("Get-ChildItem -LiteralPath $stage -File -Recurse", script, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath (Join-Path $install $exe) -PassThru", script, StringComparison.Ordinal);
        Assert.Contains("ie4uinit.exe", script, StringComparison.Ordinal);
        Assert.Contains("-ArgumentList '-show'", script, StringComparison.Ordinal);
        Assert.Contains("if ($started.HasExited)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Move-Item -LiteralPath $install", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Rename-Item -LiteralPath $install", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_PreservesUserDataFolders_AndRecordsTargetVersionOnFailure()
    {
        var script = UpdateService.BuildUpdateScript(
            @"C:\Temp\payload",
            @"C:\Raven\Client",
            1234,
            "1.0.1",
            "RavenMapPanel.exe",
            "testnonce");

        Assert.Contains("$relative.StartsWith('Config'", script, StringComparison.Ordinal);
        Assert.Contains("$relative.StartsWith('Maps'", script, StringComparison.Ordinal);
        Assert.Contains("$relative.StartsWith('CustomPrefabs'", script, StringComparison.Ordinal);
        Assert.Contains("'TARGET_VERSION=' + $targetVersion", script, StringComparison.Ordinal);
    }
}
