using System.Text.Json;
using Xunit;

namespace RavenMapPanel;

public sealed class ProjectPersistenceTests
{
    [Fact]
    public void VersionedProject_RoundTripsWithoutMachineSpecificSettings()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "roundtrip.ravenmap");
        var service = new ProjectService();
        var source = CreateSettings();

        service.Save(source, path, persistGlobal: false);

        var json = File.ReadAllText(path);
        Assert.Contains("raven-map-project", json);
        Assert.Contains("\"SchemaVersion\": 3", json);
        Assert.DoesNotContain("RustServerPath", json);
        Assert.DoesNotContain("MapsOutputPath", json);
        Assert.DoesNotContain("RecentProjects", json);

        var otherMachine = new AppSettings
        {
            RustServerPath = @"D:\OtherRust",
            MapsOutputPath = @"D:\OtherMaps",
            IconSize = 56,
            ShowRavenIcons = false,
            RecentProjects = [@"D:\previous.ravenmap"]
        };
        var loaded = service.Load(path, otherMachine, persistGlobal: false);

        Assert.Equal(@"D:\OtherRust", loaded.RustServerPath);
        Assert.Equal(@"D:\OtherMaps", loaded.MapsOutputPath);
        Assert.Equal(56, loaded.IconSize);
        Assert.False(loaded.ShowRavenIcons);
        Assert.Equal("TestHarita", loaded.MapName);
        Assert.Equal(123456, loaded.Seed);
        Assert.Equal(4250, loaded.WorldSize);
        Assert.True(loaded.TerrainFastCheck);
        Assert.True(loaded.TerrainDeepCheck);
        Assert.False(loaded.TerrainSafeAutoRepair);
        Assert.True(loaded.StrictMonumentRules);
        Assert.True(loaded.TerrainDraft.Enabled);
        Assert.Equal(TerrainBrushKind.Arctic, loaded.TerrainDraft.ActiveBrush);
        Assert.Equal(2, loaded.TerrainDraft.Strokes.Count);
        Assert.Equal(TerrainBrushKind.Mountain, loaded.TerrainDraft.Strokes[1].Kind);
        Assert.Equal(RuleState.Required, loaded.Rules["outpost"].State);
    }

    [Fact]
    public void LegacyProject_LoadsButDoesNotImportOldMachinePaths()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "legacy.ravenmap");
        var legacy = CreateSettings();
        legacy.RustServerPath = @"C:\OldMachine\Rust";
        legacy.MapsOutputPath = @"C:\OldMachine\Maps";
        File.WriteAllText(path, JsonSerializer.Serialize(legacy));

        var current = new AppSettings
        {
            RustServerPath = @"E:\Current\Rust",
            MapsOutputPath = @"E:\Current\Maps",
            IconSize = 64
        };
        var loaded = new ProjectService().Load(path, current, persistGlobal: false);

        Assert.Equal(@"E:\Current\Rust", loaded.RustServerPath);
        Assert.Equal(@"E:\Current\Maps", loaded.MapsOutputPath);
        Assert.Equal(64, loaded.IconSize);
        Assert.Equal("TestHarita", loaded.MapName);
        Assert.Equal(123456, loaded.Seed);
    }

    [Fact]
    public void FutureProjectSchema_IsRejectedWithoutChangingCurrentState()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "future.ravenmap");
        var project = ProjectService.ToDocument(CreateSettings());
        project.SchemaVersion = ProjectService.CurrentSchemaVersion + 1;
        File.WriteAllText(path, JsonSerializer.Serialize(project));

        var error = Assert.Throws<InvalidDataException>(
            () => new ProjectService().Load(path, new AppSettings(), persistGlobal: false));

        Assert.Contains("desteklenmiyor", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VersionTwoProject_LoadsWithEmptyTerrainDraft()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "v2.ravenmap");
        var project = ProjectService.ToDocument(CreateSettings());
        project.SchemaVersion = 2;
        project.TerrainDraft = null!;
        File.WriteAllText(path, JsonSerializer.Serialize(project));

        var loaded = new ProjectService().Load(path, new AppSettings(), persistGlobal: false);

        Assert.NotNull(loaded.TerrainDraft);
        Assert.Empty(loaded.TerrainDraft.Strokes);
        Assert.False(loaded.TerrainDraft.Enabled);
    }

    [Fact]
    public void Fingerprint_IgnoresGlobalSettingsButDetectsProjectChanges()
    {
        var service = new ProjectService();
        var settings = CreateSettings();
        var baseline = service.Fingerprint(settings);

        settings.RustServerPath = @"Z:\MovedRust";
        settings.MapsOutputPath = @"Z:\MovedMaps";
        Assert.Equal(baseline, service.Fingerprint(settings));

        settings.Seed++;
        Assert.NotEqual(baseline, service.Fingerprint(settings));
    }

    [Fact]
    public void LegacyAggregateRules_AreExpandedToExactVariants()
    {
        using var temp=new TempDirectory();
        var path=Path.Combine(temp.Path,"legacy-variants.ravenmap");
        var project=ProjectService.ToDocument(CreateSettings());
        project.Rules["stables"]=new RuleSetting { State=RuleState.Blocked,Minimum=2,Maximum=4,CustomPrefabVariantId="old-local" };
        project.Rules["fishing_village"]=new RuleSetting { State=RuleState.Required,Minimum=1,Maximum=3 };
        project.Rules["caves"]=new RuleSetting { State=RuleState.Blocked,Minimum=0,Maximum=2 };
        project.Rules["power_substations"]=new RuleSetting { State=RuleState.Required,Minimum=1,Maximum=4 };
        project.Rules["cave_small_easy"]=new RuleSetting { State=RuleState.Required,Minimum=1,Maximum=1 };
        File.WriteAllText(path,JsonSerializer.Serialize(project));

        var loaded=new ProjectService().Load(path,new AppSettings(),persistGlobal:false);

        Assert.Equal(RuleState.Blocked,loaded.Rules["stables_a"].State);
        Assert.Equal(RuleState.Blocked,loaded.Rules["stables_b"].State);
        Assert.Equal(RuleState.Required,loaded.Rules["fishing_village_c"].State);
        Assert.Equal(RuleState.Required,loaded.Rules["cave_small_easy"].State);
        Assert.Equal(RuleState.Blocked,loaded.Rules["cave_large_sewers_hard"].State);
        Assert.Equal(RuleState.Required,loaded.Rules["power_sub_big_1"].State);
        Assert.Equal(RuleState.Required,loaded.Rules["power_sub_small_2"].State);
        Assert.Empty(loaded.Rules["stables_a"].CustomPrefabVariantId);
        Assert.False(loaded.Rules.ContainsKey("caves"));
        Assert.False(loaded.Rules.ContainsKey("power_substations"));
    }

    private static AppSettings CreateSettings() => new()
    {
        RustServerPath = @"C:\Raven\Rust",
        MapsOutputPath = @"C:\Raven\Maps",
        RecentProjects = [@"C:\Raven\old.ravenmap"],
        MapName = "TestHarita",
        Seed = 123456,
        WorldSize = 4250,
        GenerationCount = 3,
        BiomeArid = 35,
        TerrainFastCheck = true,
        TerrainDeepCheck = true,
        TerrainSafeAutoRepair = false,
        StrictMonumentRules = true,
        TerrainDraft = new TerrainDraft
        {
            Enabled = true,
            ActiveBrush = TerrainBrushKind.Arctic,
            BrushRadius = 0.12,
            Strokes =
            [
                new TerrainStroke { StrokeId = "a", Kind = TerrainBrushKind.Arctic, X = 0.2, Y = 0.3, Radius = 0.1 },
                new TerrainStroke { StrokeId = "b", Kind = TerrainBrushKind.Mountain, X = 0.7, Y = 0.6, Radius = 0.08 }
            ]
        },
        Rules = new Dictionary<string, RuleSetting>(StringComparer.OrdinalIgnoreCase)
        {
            ["outpost"] = new()
            {
                State = RuleState.Required,
                Minimum = 1,
                Maximum = 2,
                CustomPrefabVariantId = "gold"
            }
        }
    };

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "RavenProjectTests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
