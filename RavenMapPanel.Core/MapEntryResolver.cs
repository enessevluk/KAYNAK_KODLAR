using System.Text.Json;

namespace RavenMapPanel;

internal sealed record MapReportData(
    int WorldSize,
    int MapResolution,
    int RenderOffset,
    int ImageWidth,
    int ImageHeight,
    List<MapEntry> Entries);

internal static class MapReportReader
{
    public static MapReportData Read(string path, int fallbackSize)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Harita doğrulama raporu bulunamadı.", path);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            int I(string name, int fallback = 0) => root.TryGetProperty(name, out var p) && p.TryGetInt32(out var value) ? value : fallback;

            if (!root.TryGetProperty("entries", out var array) || array.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Harita doğrulama raporunda entries alanı yok: {Path.GetFileName(path)}");

            var entries = new List<MapEntry>();
            foreach (var e in array.EnumerateArray())
            {
                string S(string name) => e.TryGetProperty(name, out var p) ? p.GetString() ?? "" : "";
                double D(string name) => e.TryGetProperty(name, out var p) && p.TryGetDouble(out var value) ? value : 0d;
                var source = S("source");
                var confidence = SourceConfidence(source);
                entries.Add(new MapEntry(S("category"), S("displayName"), S("rawName"), source,
                    D("x"), D("y"), D("z"), confidence));
            }

            return new MapReportData(
                I("worldSize", fallbackSize),
                I("mapResolution"),
                I("renderOffset", -1),
                I("imageWidth"),
                I("imageHeight"),
                entries);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Harita doğrulama raporu geçersiz JSON içeriyor: {Path.GetFileName(path)}", ex);
        }
    }

    private static double SourceConfidence(string source)
    {
        if (source.Contains("terrainmeta", StringComparison.OrdinalIgnoreCase)) return 5;
        if (source.Contains("runtime_scene", StringComparison.OrdinalIgnoreCase)) return 4;
        if (source.Contains("saved_map_godrock_exact", StringComparison.OrdinalIgnoreCase)) return 3;
        if (source.Contains("saved_map", StringComparison.OrdinalIgnoreCase)) return 2;
        if (source.Contains("renderer", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }
}

/// <summary>
/// Tek bir kategori/god-rock çözümleme kaynağı. Hem PNG overlay hem canlı harita
/// bu sınıfın ürettiği aynı resolved entry listesini kullanır.
/// </summary>
internal sealed class MapEntryResolver(IReadOnlyList<MonumentRule> catalog)
{
    private readonly Dictionary<string, MonumentRule> byId = catalog
        .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MapEntry> Merge(IReadOnlyList<MapEntry> main, IReadOnlyList<MapEntry> world)
    {
        var normal = new List<MapEntry>();
        foreach (var entry in world.Concat(main))
        {
            var category = ResolveCategory(entry);
            if (category.Length == 0 || category.Equals("god_rocks", StringComparison.OrdinalIgnoreCase))
                continue;

            var resolved = entry with { Category = category };
            var radius = DedupeRadius(category);
            if (!normal.Any(x => x.Category.Equals(category, StringComparison.OrdinalIgnoreCase) && Distance(x, resolved) < radius))
                normal.Add(resolved);
        }

        var rocks = world.Concat(main)
            .Select(x => x with { Category = ResolveCategory(x) })
            .Where(x => x.Category.Equals("god_rocks", StringComparison.OrdinalIgnoreCase))
            .Where(IsExactGodRock)
            .OrderByDescending(x => x.Confidence)
            .ToList();

        foreach (var rock in rocks)
        {
            if (!normal.Any(x => x.Category.Equals("god_rocks", StringComparison.OrdinalIgnoreCase) && Distance(x, rock) < DedupeRadius("god_rocks")))
                normal.Add(rock with { Category = "god_rocks" });
        }

        return normal;
    }

    public string ResolveCategory(MapEntry entry)
    {
        var raw = NormalizeIdentity(entry.RawName);
        var rawBase = PrefabBase(raw);
        var name = NormalizeIdentity(entry.Name);

        // Bir prefab adı başka bir prefab adını içerebilir. Örneğin Rust'ın
        // Sewer Branch kökü `radtown_small_3.prefab`, Radtown kökü ise
        // `radtown_1.prefab` adını taşır. Kısa bir "contains" araması Sewer
        // Branch'i yanlışlıkla Radtown'a dönüştürür. Bu yüzden önce tam yol ve
        // tam dosya adını, ancak ardından en uzun parçalı eşleşmeyi kullanırız.
        var exact = catalog
            .Select(rule => (Rule: rule, Score: ExactMatchScore(rule, raw, rawBase, name)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();
        if (exact.Rule is not null) return exact.Rule.Id;

        // Eski raporlar geçerli görünen fakat artık yanlış olduğu bilinen bir
        // kategori taşıyabilir (ör. swamp_c daha önce "swamps" yazılıyordu).
        // Ham prefab tam eşleşmediyse raporun kategorisini güvenli geri dönüş
        // olarak kullan. Böylece eski haritalar yeniden üretilmeden düzelir.
        var category = NormalizeCategory(entry.Category);
        if (byId.ContainsKey(category)) return category;

        var haystack = raw + " " + name;
        var fallback = catalog
            .SelectMany(rule => rule.Prefabs
                .Concat(rule.Aliases)
                .Append(rule.Id)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => (Rule: rule, Value: NormalizeIdentity(value))))
            .Where(x => x.Value.Length > 2 && haystack.Contains(x.Value, StringComparison.Ordinal))
            .OrderByDescending(x => x.Value.Length)
            .FirstOrDefault();
        return fallback.Rule?.Id ?? "";
    }

    private static int ExactMatchScore(MonumentRule rule, string raw, string rawBase, string name)
    {
        var best = 0;
        foreach (var value in rule.Prefabs.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var normalized = NormalizeIdentity(value);
            var valueBase = PrefabBase(normalized);
            if (raw.Length > 0 && StripPrefabExtension(raw) == StripPrefabExtension(normalized))
                best = Math.Max(best, 4000 + normalized.Length);
            if (rawBase.Length > 0 && rawBase == valueBase)
                best = Math.Max(best, 3000 + valueBase.Length);
        }

        foreach (var value in rule.Aliases.Append(rule.Id).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var normalized = NormalizeIdentity(value);
            if (name == normalized || rawBase == PrefabBase(normalized))
                best = Math.Max(best, 2000 + normalized.Length);
        }
        return best;
    }

    private static string NormalizeIdentity(string value) =>
        (value ?? "").Trim().Replace('\\', '/').ToLowerInvariant();

    private static string StripPrefabExtension(string value) =>
        value.EndsWith(".prefab", StringComparison.Ordinal) ? value[..^7] : value;

    private static string PrefabBase(string value)
    {
        var normalized = StripPrefabExtension(NormalizeIdentity(value));
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    public static string NormalizeCategory(string category) =>
        category.Equals("mining_outpost", StringComparison.OrdinalIgnoreCase) ? "warehouse" : category.ToLowerInvariant();

    public static bool IsExactGodRock(MapEntry entry)
    {
        if (entry.Source.Contains("terrainmeta", StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains("runtime_scene", StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains("saved_map_godrock_exact", StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains("saved_map_godrock_root_a_exact", StringComparison.OrdinalIgnoreCase))
            return true;

        var identity = (entry.RawName + " " + entry.Name).Replace('\\', '/').ToLowerInvariant();
        if (identity.Contains("assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_a.prefab"))
            return true;
        if (identity.Contains("anvil rock") || identity.Contains("anvil_rock")
            || identity.Contains("arch rock") || identity.Contains("arch_rock"))
            return false;
        return identity.Contains("godrock") || identity.Contains("god_rock") || identity.Contains("god rock");
    }

    public static double DedupeRadius(string category) => category switch
    {
        "powerlines" => 12d,
        "god_rocks" => 60d,
        _ => 10d
    };

    public static double Distance(MapEntry a, MapEntry b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
}
