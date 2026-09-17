using System.Text.Json.Serialization;

namespace RavenMapPanel;

public enum RuleState { Optional, Required, Blocked }

public enum TerrainBrushKind { Sea, Temperate, Dirt, Arctic, Mountain, Lake }

/// <summary>
/// Editörde çizilen tek bir normalize arazi fırça örneği. Koordinatlar ve yarıçap
/// 0..1 aralığında tutulduğu için taslak farklı dünya boyutlarına taşınabilir.
/// </summary>
public sealed class TerrainStroke
{
    public string StrokeId { get; set; } = Guid.NewGuid().ToString("N");
    public TerrainBrushKind Kind { get; set; } = TerrainBrushKind.Temperate;
    public double X { get; set; }
    public double Y { get; set; }
    public double Radius { get; set; } = 0.08;
    public double Strength { get; set; } = 1;
}

public sealed class TerrainDraft
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public double BrushRadius { get; set; } = 0.08;
    public TerrainBrushKind ActiveBrush { get; set; } = TerrainBrushKind.Temperate;
    public List<TerrainStroke> Strokes { get; set; } = [];
}

public sealed class MonumentRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Icon { get; set; } = "";
    public bool RenderOnMap { get; set; } = true;
    public List<string> Prefabs { get; set; } = [];
    public List<string> Aliases { get; set; } = [];
    public bool IsCustom { get; set; }
    public bool CustomPrefabAvailable { get; set; }
    // Dünya üreticisi tarafından yönetilen yollar/hatlar/çevre öğeleri monument
    // kuralı değildir. Katalogda harita algılama ve ikon çizimi için tutulur.
    public bool IsWorldFeature { get; set; }
    // Runtime-only custom prefab selection. Empty means Vanilla.
    [JsonIgnore] public string CustomPrefabVariantId { get; set; } = "";
    // Legacy compatibility for older .ravenmap/settings files and callers.
    [JsonIgnore] public bool UseCustomPrefab
    {
        get => !string.IsNullOrWhiteSpace(CustomPrefabVariantId);
        set
        {
            if (!value) CustomPrefabVariantId = "";
            else if (string.IsNullOrWhiteSpace(CustomPrefabVariantId)) CustomPrefabVariantId = "custom";
        }
    }
    [JsonIgnore] public RuleState State { get; set; }
    [JsonIgnore] public int Minimum { get; set; }
    [JsonIgnore] public int Maximum { get; set; } = 99;
}

public sealed class AppSettings
{
    public string RustServerPath { get; set; } = @"C:\RavenMapServer\RustServer";
    public string Identity { get; set; } = "raven_native";
    public string MapName { get; set; } = "RavenHarita";
    public int Seed { get; set; } = Random.Shared.Next(1, int.MaxValue);
    public int WorldSize { get; set; } = 4500;
    public int GenerationCount { get; set; } = 1;
    public bool Roads { get; set; } = true;
    public bool Rivers { get; set; } = true;
    public bool PowerLines { get; set; } = true;
    public bool Rails { get; set; } = true;
    public bool UndergroundRails { get; set; } = true;
    public bool Oasis { get; set; }
    public bool Canyons { get; set; }
    public bool Lakes { get; set; }

    // Advanced CustomGenerator percentages. Tier values are relative weights and
    // are normalized by CustomGenerator. Main biome values are corrected by
    // RavenBiomeFix; Jungle remains an independent conversion ratio.
    public double Tier0 { get; set; } = 30;
    public double Tier1 { get; set; } = 30;
    public double Tier2 { get; set; } = 40;
    public double BiomeArid { get; set; } = 40;
    public double BiomeTemperate { get; set; } = 15;
    public double BiomeTundra { get; set; } = 15;
    public double BiomeArctic { get; set; } = 30;
    public double BiomeJungle { get; set; } = 70;

    // Terrain safety is deliberately time-budgeted. The fast pass reuses the
    // serialized prefab scan; deep inspection remains opt-in. Repair is allowed
    // only for an explicitly verified prefab profile, never for an unknown rock.
    public bool TerrainFastCheck { get; set; } = true;
    public bool TerrainDeepCheck { get; set; }
    public bool TerrainSafeAutoRepair { get; set; } = true;
    public bool StrictMonumentRules { get; set; }
    public TerrainDraft TerrainDraft { get; set; } = new();

    public double IconSize { get; set; } = 72;
    public bool ShowRavenIcons { get; set; } = true;
    public bool ShowEnvironmentIcons { get; set; } = true;
    public string CurrentProjectPath { get; set; } = "";
    public string MapsOutputPath { get; set; } = "";
    public List<string> RecentProjects { get; set; } = [];
    public Dictionary<string, RuleSetting> Rules { get; set; } = [];
}

/// <summary>
/// Makineye/kullanıcıya ait uygulama tercihleri. Bu alanlar .ravenmap proje
/// dosyasına yazılmaz; farklı bir proje açıldığında değişmemeleri gerekir.
/// </summary>
public sealed class GlobalAppSettings
{
    public string Format { get; set; } = "raven-global-settings";
    public int SchemaVersion { get; set; } = 2;
    public string RustServerPath { get; set; } = @"C:\RavenMapServer\RustServer";
    public string Identity { get; set; } = "raven_native";
    public string MapsOutputPath { get; set; } = "";
    public double IconSize { get; set; } = 72;
    public bool ShowRavenIcons { get; set; } = true;
    public bool ShowEnvironmentIcons { get; set; } = true;
    public string CurrentProjectPath { get; set; } = "";
    public List<string> RecentProjects { get; set; } = [];
}

/// <summary>
/// Taşınabilir ve sürümlü Raven harita projesi. Bilgisayara özgü Rust/output
/// yolları ile son kullanılan proje listesi bilinçli olarak bu modelde yoktur.
/// </summary>
public sealed class RavenMapProject
{
    public string Format { get; set; } = "raven-map-project";
    public int SchemaVersion { get; set; } = 3;
    public string MapName { get; set; } = "RavenHarita";
    public int Seed { get; set; } = Random.Shared.Next(1, int.MaxValue);
    public int WorldSize { get; set; } = 4500;
    public int GenerationCount { get; set; } = 1;
    public bool Roads { get; set; } = true;
    public bool Rivers { get; set; } = true;
    public bool PowerLines { get; set; } = true;
    public bool Rails { get; set; } = true;
    public bool UndergroundRails { get; set; } = true;
    public bool Oasis { get; set; }
    public bool Canyons { get; set; }
    public bool Lakes { get; set; }
    public double Tier0 { get; set; } = 30;
    public double Tier1 { get; set; } = 30;
    public double Tier2 { get; set; } = 40;
    public double BiomeArid { get; set; } = 40;
    public double BiomeTemperate { get; set; } = 15;
    public double BiomeTundra { get; set; } = 15;
    public double BiomeArctic { get; set; } = 30;
    public double BiomeJungle { get; set; } = 70;
    public bool TerrainFastCheck { get; set; } = true;
    public bool TerrainDeepCheck { get; set; }
    public bool TerrainSafeAutoRepair { get; set; } = true;
    public bool StrictMonumentRules { get; set; }
    public TerrainDraft TerrainDraft { get; set; } = new();
    public Dictionary<string, RuleSetting> Rules { get; set; } = [];
}

public sealed class RuleSetting
{
    public RuleState State { get; set; }
    public int Minimum { get; set; }
    public int Maximum { get; set; } = 99;
    // Kept so old projects that only stored a boolean still migrate to the single direct custom set.
    public bool UseCustomPrefab { get; set; }
    public string CustomPrefabVariantId { get; set; } = "";
}

public sealed record CustomPrefabAnchor(double X, double Y, double Z, double Yaw = 0);

public sealed class CustomPrefabTarget
{
    public string SourcePath { get; init; } = "";
    public string ContentId { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string TargetFileName { get; init; } = "";
    public CustomPrefabAnchor? Anchor { get; init; }
}

public sealed class CustomPrefabVariant
{
    public string MonumentId { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public string PreviewPath { get; init; } = "";
    public string MinimumPlan { get; init; } = "STANDARD";
    public bool IsRemote { get; init; }
    public IReadOnlyList<CustomPrefabTarget> Targets { get; init; } = [];
    public bool IsValid { get; init; }
    public string ValidationMessage { get; init; } = "";
    [JsonIgnore] public string DisplayName => IsValid ? Name : $"⚠ {Name}";
}

public sealed record MapEntry(string Category, string Name, string RawName, string Source,
    double X, double Y, double Z, double Confidence = 0);

public sealed record GenerationResult(string MapPath, string ImagePath, string OutputFolder,
    int IconCount, int GodRockCount, string RawImagePath = "", string ReportFolder = "", int WorldSize = 0, int Seed = 0,
    string BackupPath = "");

public sealed class GenerationHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string MapName { get; set; } = "RavenHarita";
    public int Seed { get; set; }
    public int WorldSize { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public int IconCount { get; set; }
    public int GodRockCount { get; set; }
    public int ValidationIssues { get; set; }
    public string MapPath { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string RawImagePath { get; set; } = "";
    public string ReportFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public bool IsImported { get; set; }

    [JsonIgnore] public string DisplayName => $"{MapName} · {WorldSize} · {Seed}";
    [JsonIgnore] public string GeneratedText => GeneratedAt.ToString("dd.MM.yyyy · HH:mm");
    [JsonIgnore] public string SummaryText => $"God Rock {GodRockCount}";
    [JsonIgnore] public string ValidationText => ValidationIssues == 0 ? "Doğrulama başarılı" : $"{ValidationIssues} sorun";
    [JsonIgnore] public string ValidationColor => ValidationIssues == 0 ? "#6FDBAE" : "#FF6868";
    [JsonIgnore] public string BackupText => string.IsNullOrWhiteSpace(BackupPath) ? "" : "Önceki sürüm yedeklendi";
    [JsonIgnore] public bool HasBackup => !string.IsNullOrWhiteSpace(BackupPath) && Directory.Exists(BackupPath);
    [JsonIgnore] public string SourceText => IsImported ? "İÇE AKTARILDI" : "RAVEN ÜRETİMİ";
    [JsonIgnore] public string SourceColor => IsImported ? "#8DD8F0" : "#83E0B3";
    [JsonIgnore] public string SourceBackground => IsImported ? "#15303A" : "#153126";
}

public sealed class MapComparisonRow
{
    public string Name { get; set; } = "";
    public int CountA { get; set; }
    public int CountB { get; set; }
    [JsonIgnore] public int Difference => CountB - CountA;
    [JsonIgnore] public string DifferenceText => Difference == 0 ? "—" : Difference > 0 ? $"+{Difference}" : Difference.ToString();
    [JsonIgnore] public string DifferenceColor => Difference == 0 ? "#7F8B98" : Difference > 0 ? "#6FDBAE" : "#FF6868";
}
