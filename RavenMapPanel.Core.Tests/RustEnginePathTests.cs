using Xunit;

namespace RavenMapPanel;

public sealed class RustEnginePathTests
{
    [Fact]
    public void Check_FindsRustDedicatedInsideCommonRustServerChild()
    {
        var root = Path.Combine(Path.GetTempPath(), "RavenRustPathTests", Guid.NewGuid().ToString("N"));
        var rust = Path.Combine(root, "RustServer");
        var managed = Path.Combine(rust, "RustDedicated_Data", "Managed");
        Directory.CreateDirectory(managed);
        try
        {
            File.WriteAllText(Path.Combine(rust, "RustDedicated.exe"), "test");
            File.WriteAllText(Path.Combine(managed, "0Harmony.dll"), "test");
            File.WriteAllText(Path.Combine(managed, "Rust.Harmony.dll"), "test");

            var result = new RustEngineService().Check(root);

            Assert.True(result.IsReady);
            Assert.Equal(Path.GetFullPath(rust), result.RustServerPath);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void ReadInstalledBuildId_ReadsRustServerManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "RavenRustBuildTests", Guid.NewGuid().ToString("N"));
        var steamApps = Path.Combine(root, "steamapps");
        Directory.CreateDirectory(steamApps);
        try
        {
            File.WriteAllText(Path.Combine(steamApps, "appmanifest_258550.acf"),
                "\"AppState\"\n{\n  \"appid\" \"258550\"\n  \"buildid\" \"24793074\"\n}");

            Assert.Equal("24793074", RustEngineService.ReadInstalledBuildId(root));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void ParsePublicBuildId_ReadsOnlyPublicBranch()
    {
        const string output = """
            "branches"
            {
                "public"
                {
                    "buildid" "24793074"
                }
                "staging"
                {
                    "buildid" "24912903"
                }
            }
            """;

        Assert.Equal("24793074", RustEngineService.ParsePublicBuildId(output));
    }
}
