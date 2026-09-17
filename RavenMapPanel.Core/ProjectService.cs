using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RavenMapPanel;

internal sealed class ProjectService
{
    public const int CurrentSchemaVersion = 3;
    private const string ProjectFormat = "raven-map-project";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppSettings CreateNew(AppSettings current) => new()
    {
        RustServerPath = current.RustServerPath,
        Identity = current.Identity,
        RecentProjects = current.RecentProjects.ToList(),
        MapsOutputPath = current.MapsOutputPath,
        IconSize = current.IconSize,
        ShowRavenIcons = current.ShowRavenIcons,
        ShowEnvironmentIcons = current.ShowEnvironmentIcons
    };

    public void Save(AppSettings settings, string path, bool persistGlobal = true)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Proje klasörü belirlenemedi.");
        Directory.CreateDirectory(directory);

        var document = ToDocument(settings);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(document, Options));
            File.Move(temp, path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }

        settings.CurrentProjectPath = path;
        AddRecent(settings, path);
        if (persistGlobal)
            SettingsStore.Save(settings);
    }

    public AppSettings Load(string path, AppSettings current, bool persistGlobal = true)
    {
        path = Path.GetFullPath(path);
        var document = ReadDocument(path);
        var loaded = ApplyProject(document, current);
        MigrateLegacyMonumentRules(loaded.Rules);
        loaded.CurrentProjectPath = path;
        AddRecent(loaded, path);
        if (persistGlobal)
            SettingsStore.Save(loaded);
        return loaded;
    }

    public RavenMapProject ReadDocument(string path)
    {
        path = Path.GetFullPath(path);
        var json = File.ReadAllText(path);
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        var format = root.TryGetProperty("format", out var camelFormat)
            ? camelFormat.GetString()
            : root.TryGetProperty("Format", out var pascalFormat)
                ? pascalFormat.GetString()
                : null;

        RavenMapProject project;
        if (string.Equals(format, ProjectFormat, StringComparison.OrdinalIgnoreCase))
        {
            project = JsonSerializer.Deserialize<RavenMapProject>(json, Options)
                ?? throw new InvalidDataException("Raven proje dosyası okunamadı.");
        }
        else
        {
            // v1.x .ravenmap dosyaları doğrudan AppSettings içeriyordu.
            var legacy = JsonSerializer.Deserialize<AppSettings>(json, Options)
                ?? throw new InvalidDataException("Eski Raven ayar dosyası okunamadı.");
            project = ToDocument(legacy);
        }

        ValidateDocument(project);
        return project;
    }

    public string Fingerprint(AppSettings settings)
    {
        var project = ToDocument(settings);
        project.Rules = project.Rules
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var json = JsonSerializer.Serialize(project, CompactOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, Options), Options)
        ?? throw new InvalidOperationException("Harita ayarları kopyalanamadı.");

    public static void AddRecent(AppSettings settings, string path)
    {
        settings.RecentProjects.RemoveAll(x => x.Equals(path, StringComparison.OrdinalIgnoreCase));
        settings.RecentProjects.Insert(0, path);
        if (settings.RecentProjects.Count > 10)
            settings.RecentProjects.RemoveRange(10, settings.RecentProjects.Count - 10);
    }

    internal static RavenMapProject ToDocument(AppSettings settings) => new()
    {
        Format = ProjectFormat,
        SchemaVersion = CurrentSchemaVersion,
        MapName = settings.MapName,
        Seed = settings.Seed,
        WorldSize = settings.WorldSize,
        GenerationCount = settings.GenerationCount,
        Roads = settings.Roads,
        Rivers = settings.Rivers,
        PowerLines = settings.PowerLines,
        Rails = settings.Rails,
        UndergroundRails = settings.UndergroundRails,
        Oasis = settings.Oasis,
        Canyons = settings.Canyons,
        Lakes = settings.Lakes,
        Tier0 = settings.Tier0,
        Tier1 = settings.Tier1,
        Tier2 = settings.Tier2,
        BiomeArid = settings.BiomeArid,
        BiomeTemperate = settings.BiomeTemperate,
        BiomeTundra = settings.BiomeTundra,
        BiomeArctic = settings.BiomeArctic,
        BiomeJungle = settings.BiomeJungle,
        TerrainFastCheck = settings.TerrainFastCheck,
        TerrainDeepCheck = settings.TerrainDeepCheck,
        TerrainSafeAutoRepair = settings.TerrainSafeAutoRepair,
        StrictMonumentRules = settings.StrictMonumentRules,
        TerrainDraft = CloneTerrainDraft(settings.TerrainDraft),
        Rules = CloneRules(settings.Rules)
    };

    private static AppSettings ApplyProject(RavenMapProject project, AppSettings current) => new()
    {
        RustServerPath = current.RustServerPath,
        Identity = current.Identity,
        MapsOutputPath = current.MapsOutputPath,
        IconSize = current.IconSize,
        ShowRavenIcons = current.ShowRavenIcons,
        ShowEnvironmentIcons = current.ShowEnvironmentIcons,
        CurrentProjectPath = current.CurrentProjectPath,
        RecentProjects = current.RecentProjects.ToList(),
        MapName = string.IsNullOrWhiteSpace(project.MapName) ? "RavenHarita" : project.MapName,
        Seed = project.Seed,
        WorldSize = project.WorldSize,
        GenerationCount = Math.Clamp(project.GenerationCount, 1, 7),
        Roads = project.Roads,
        Rivers = project.Rivers,
        PowerLines = project.PowerLines,
        Rails = project.Rails,
        UndergroundRails = project.UndergroundRails,
        Oasis = project.Oasis,
        Canyons = project.Canyons,
        Lakes = project.Lakes,
        Tier0 = project.Tier0,
        Tier1 = project.Tier1,
        Tier2 = project.Tier2,
        BiomeArid = project.BiomeArid,
        BiomeTemperate = project.BiomeTemperate,
        BiomeTundra = project.BiomeTundra,
        BiomeArctic = project.BiomeArctic,
        BiomeJungle = project.BiomeJungle,
        TerrainFastCheck = project.TerrainFastCheck,
        TerrainDeepCheck = project.TerrainDeepCheck,
        TerrainSafeAutoRepair = project.TerrainSafeAutoRepair,
        StrictMonumentRules = project.StrictMonumentRules,
        TerrainDraft = CloneTerrainDraft(project.TerrainDraft),
        Rules = CloneRules(project.Rules)
    };

    private static TerrainDraft CloneTerrainDraft(TerrainDraft? draft)
    {
        draft ??= new TerrainDraft();
        return new TerrainDraft
        {
            Version = Math.Max(1, draft.Version),
            Enabled = draft.Enabled,
            BrushRadius = draft.BrushRadius,
            ActiveBrush = draft.ActiveBrush,
            Strokes = (draft.Strokes ?? [])
                .Select(x => new TerrainStroke
                {
                    StrokeId = string.IsNullOrWhiteSpace(x.StrokeId) ? Guid.NewGuid().ToString("N") : x.StrokeId,
                    Kind = x.Kind,
                    X = x.X,
                    Y = x.Y,
                    Radius = x.Radius,
                    Strength = x.Strength
                })
                .ToList()
        };
    }

    private static Dictionary<string, RuleSetting> CloneRules(
        IReadOnlyDictionary<string, RuleSetting>? rules) =>
        (rules ?? new Dictionary<string, RuleSetting>())
        .ToDictionary(
            x => x.Key,
            x => new RuleSetting
            {
                State = x.Value.State,
                Minimum = x.Value.Minimum,
                Maximum = x.Value.Maximum,
                UseCustomPrefab = x.Value.UseCustomPrefab,
                CustomPrefabVariantId = x.Value.CustomPrefabVariantId
            },
            StringComparer.OrdinalIgnoreCase);

    private static void MigrateLegacyMonumentRules(Dictionary<string,RuleSetting> rules)
    {
        Expand("stables",["stables_a","stables_b"]);
        Expand("fishing_village",["fishing_village_a","fishing_village_b","fishing_village_c"]);
        Expand("caves",[
            "cave_small_easy","cave_medium_easy","cave_small_medium",
            "cave_medium_medium","cave_large_medium","cave_small_hard",
            "cave_medium_hard","cave_large_hard","cave_large_sewers_hard"
        ]);
        Expand("power_substations",[
            "power_sub_big_1","power_sub_big_2","power_sub_small_1","power_sub_small_2"
        ]);

        void Expand(string legacyId,IReadOnlyList<string> newIds)
        {
            if (!rules.TryGetValue(legacyId,out var old)) return;
            foreach (var id in newIds)
                if (!rules.ContainsKey(id)) rules[id]=new RuleSetting
                {
                    State=old.State, Minimum=old.Minimum, Maximum=old.Maximum,
                    UseCustomPrefab=false, CustomPrefabVariantId=""
                };
            rules.Remove(legacyId);
        }

    }

    private static void ValidateDocument(RavenMapProject project)
    {
        if (!string.Equals(project.Format, ProjectFormat, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Dosya Raven Map projesi değil.");
        if (project.SchemaVersion <= 0 || project.SchemaVersion > CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Bu proje sürümü desteklenmiyor ({project.SchemaVersion}). Raven Map Panel'i güncelleyin.");
        if (project.Seed <= 0)
            throw new InvalidDataException("Proje seed değeri geçersiz.");
        if (project.WorldSize is < 1000 or > 6000)
            throw new InvalidDataException("Proje dünya boyutu 1000–6000 aralığında olmalıdır.");
    }
}
