using System.Text.Json;

namespace RavenMapPanel;

internal static class SettingsStore
{
    private const int CurrentGlobalSchemaVersion = 2;
    private const string GlobalFormat = "raven-global-settings";
    public static string AppRoot => Path.GetFullPath(AppContext.BaseDirectory);
    public static string ConfigRoot => Ensure(Path.Combine(AppRoot, "Config"));
    public static string DefaultMapsRoot => Ensure(Path.Combine(AppRoot, "Maps"));
    public static string CustomIconsRoot => Path.Combine(ConfigRoot, "Icons");
    public static string MonumentGalleryRoot => Path.Combine(ConfigRoot, "MonumentGallery");
    public static string CustomPrefabsRoot => Ensure(Path.Combine(AppRoot, "CustomPrefabs"));
    public static string LegacyCustomPrefabsRoot => Path.Combine(ConfigRoot, "CustomPrefabs");
    public static string CustomCatalogPath => Path.Combine(ConfigRoot, "custom_monuments.json");
    public static string GenerationHistoryPath => Path.Combine(ConfigRoot, "generation_history.json");
    private static string FilePath => Path.Combine(ConfigRoot, "settings.json");
    private static string LegacyFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RavenMapPanel", "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string LastLoadWarning { get; private set; } = "";

    public static AppSettings Load()
    {
        string? source = null;
        LastLoadWarning = "";
        try
        {
            source = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            if (!File.Exists(source)) return new();

            var json = File.ReadAllText(source);
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            var format = root.TryGetProperty("format", out var camelFormat)
                ? camelFormat.GetString()
                : root.TryGetProperty("Format", out var pascalFormat)
                    ? pascalFormat.GetString()
                    : null;

            if (string.Equals(format, GlobalFormat, StringComparison.OrdinalIgnoreCase))
            {
                var global = JsonSerializer.Deserialize<GlobalAppSettings>(json, Options) ?? new();
                if (global.SchemaVersion <= 0 || global.SchemaVersion > CurrentGlobalSchemaVersion)
                    throw new InvalidDataException($"Ayar formatı desteklenmiyor ({global.SchemaVersion}).");

                var settings = FromGlobal(global);
                // v1.6.0 öncesinde ikon varsayılanı 42 px idi. Mevcut global ayar dosyası
                // şema v2 olsa bile bu eski varsayılanı bir defaya mahsus 72 px’e taşı.
                if (Math.Abs(global.IconSize - 42d) < 0.001d)
                    Save(settings);
                if (!string.IsNullOrWhiteSpace(settings.CurrentProjectPath) &&
                    File.Exists(settings.CurrentProjectPath))
                {
                    try
                    {
                        settings = new ProjectService().Load(
                            settings.CurrentProjectPath, settings, persistGlobal: false);
                    }
                    catch
                    {
                        // Global tercihler bozuk/gelecek sürüm bir proje yüzünden kaybolmamalı.
                        // Kullanıcı proje dosyasını son kullanılanlar listesinden tekrar deneyebilir.
                    }
                }
                return settings;
            }

            // v1.x settings.json bütün proje ve makine ayarlarını tek modelde tutuyordu.
            var legacy = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new();
            MigrateProjectPath(legacy);
            PreserveLegacyDraft(legacy);
            BackupLegacySettings(source);
            if (Math.Abs(legacy.IconSize - 42d) < 0.001d) legacy.IconSize = 72d;
            Save(legacy);
            return legacy;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(source) && File.Exists(source))
            {
                try
                {
                    var backup = Path.Combine(ConfigRoot, $"settings.corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                    File.Copy(source, backup, false);
                    LastLoadWarning = $"Ayar dosyası okunamadı; bozuk dosya yedeklendi: {backup}. Hata: {ex.Message}";
                }
                catch
                {
                    LastLoadWarning = $"Ayar dosyası okunamadı: {source}. Hata: {ex.Message}";
                }
            }
            else LastLoadWarning = "Ayarlar yüklenemedi: " + ex.Message;
            return new();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(ConfigRoot);
        var global = new GlobalAppSettings
        {
            Format = GlobalFormat,
            SchemaVersion = CurrentGlobalSchemaVersion,
            RustServerPath = settings.RustServerPath,
            Identity = settings.Identity,
            MapsOutputPath = settings.MapsOutputPath,
            IconSize = settings.IconSize,
            ShowRavenIcons = settings.ShowRavenIcons,
            ShowEnvironmentIcons = settings.ShowEnvironmentIcons,
            CurrentProjectPath = settings.CurrentProjectPath,
            RecentProjects = settings.RecentProjects.ToList()
        };
        var temp = FilePath + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(global, Options));
            File.Move(temp, FilePath, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public static string OutputRoot(AppSettings settings)
    {
        var selected = settings.MapsOutputPath?.Trim();
        return Ensure(string.IsNullOrWhiteSpace(selected) ? DefaultMapsRoot : Path.GetFullPath(selected));
    }

    public static string DefaultProjectPath(string name) => Path.Combine(ConfigRoot, SafeName(name, "RavenHarita") + ".ravenmap");
    public static string SafeName(string? value, string fallback)
    {
        var safe = string.Concat((value ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }
    private static void MigrateProjectPath(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.CurrentProjectPath)) return;
        var oldPath = Path.GetFullPath(settings.CurrentProjectPath);
        var target = Path.Combine(ConfigRoot, Path.GetFileName(oldPath));
        if (File.Exists(oldPath) && string.Equals(Path.GetDirectoryName(oldPath)?.TrimEnd('\\'), AppRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(target)) File.Copy(oldPath, target);
            settings.CurrentProjectPath = target;
        }
        else if (!File.Exists(oldPath))
        {
            settings.CurrentProjectPath = File.Exists(target) ? target : "";
        }
        settings.RecentProjects.RemoveAll(x => x.Equals(oldPath, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(settings.CurrentProjectPath) &&
            !settings.RecentProjects.Contains(settings.CurrentProjectPath, StringComparer.OrdinalIgnoreCase))
            settings.RecentProjects.Insert(0, settings.CurrentProjectPath);
    }

    private static AppSettings FromGlobal(GlobalAppSettings global) => new()
    {
        RustServerPath = global.RustServerPath,
        Identity = global.Identity,
        MapsOutputPath = global.MapsOutputPath,
        IconSize = Math.Abs(global.IconSize - 42d) < 0.001d
            ? 72d
            : Math.Clamp(global.IconSize <= 0 ? 72d : global.IconSize, 24d, 72d),
        ShowRavenIcons = global.ShowRavenIcons,
        ShowEnvironmentIcons = global.ShowEnvironmentIcons,
        CurrentProjectPath = global.CurrentProjectPath,
        RecentProjects = global.RecentProjects?.ToList() ?? []
    };

    private static void BackupLegacySettings(string source)
    {
        try
        {
            if (!File.Exists(source)) return;
            var backup = Path.Combine(ConfigRoot, "settings.v1.legacy-backup.json");
            if (!File.Exists(backup)) File.Copy(source, backup);
        }
        catch { }
    }

    private static void PreserveLegacyDraft(AppSettings legacy)
    {
        try
        {
            var projects = new ProjectService();
            var needsRecovery = string.IsNullOrWhiteSpace(legacy.CurrentProjectPath)
                || !File.Exists(legacy.CurrentProjectPath);

            if (!needsRecovery)
            {
                var saved = projects.Load(
                    legacy.CurrentProjectPath, legacy, persistGlobal: false);
                needsRecovery = !string.Equals(
                    projects.Fingerprint(saved),
                    projects.Fingerprint(legacy),
                    StringComparison.Ordinal);
            }

            if (!needsRecovery) return;

            var name = SafeName(legacy.MapName, "RavenHarita");
            var recoveryPath = Path.Combine(ConfigRoot, name + ".recovered-v1.ravenmap");
            projects.Save(legacy, recoveryPath, persistGlobal: false);
        }
        catch
        {
            // Eski settings yedeği ayrıca saklanır; recovery üretilememesi açılışı engellemez.
        }
    }

    private static string Ensure(string path) { Directory.CreateDirectory(path); return path; }
}

internal static class GenerationHistoryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static List<GenerationHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(SettingsStore.GenerationHistoryPath))
                return [];
            return JsonSerializer.Deserialize<List<GenerationHistoryEntry>>(
                File.ReadAllText(SettingsStore.GenerationHistoryPath), Options) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Add(GenerationHistoryEntry entry)
    {
        var items = LoadExisting();
        items.RemoveAll(x => string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
        items.Insert(0, entry);
        if (items.Count > 60)
            items.RemoveRange(60, items.Count - 60);
        Save(items);
    }

    public static List<GenerationHistoryEntry> LoadExisting()
    {
        var items = Load();
        var existing = items.Where(IsOutputAvailable).ToList();

        // Maps klasöründen çıktı klasörü veya .map dosyası elle silindiyse
        // geçmiş kaydı da otomatik temizlenir.
        if (existing.Count != items.Count)
            Save(existing);

        return existing;
    }

    public static void Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        var items = Load();
        var removed = items.RemoveAll(
            x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
            Save(items);
    }

    private static bool IsOutputAvailable(GenerationHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.OutputFolder) ||
            !Directory.Exists(entry.OutputFolder))
            return false;

        // Yeni kayıtların .map dosyası teslim klasörünün içinde olur.
        // Map dosyası elle silinirse klasör kalsa bile geçmişte göstermeyiz.
        if (!string.IsNullOrWhiteSpace(entry.MapPath) &&
            !File.Exists(entry.MapPath))
            return false;

        return true;
    }

    public static void Save(IEnumerable<GenerationHistoryEntry> items)
    {
        Directory.CreateDirectory(SettingsStore.ConfigRoot);
        File.WriteAllText(SettingsStore.GenerationHistoryPath,
            JsonSerializer.Serialize(items, Options));
    }
}
