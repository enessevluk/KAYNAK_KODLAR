namespace RavenMapPanel;

internal static class GenerationPreflight
{
    private const int MaximumTerrainSamples = 50_000;

    public static IReadOnlyList<string> Validate(
        AppSettings settings,
        IReadOnlyList<MonumentRule> catalog)
    {
        var issues = new List<string>();

        if (settings.Seed <= 0)
            issues.Add("Seed sıfırdan büyük olmalıdır.");
        if (settings.WorldSize is < 1000 or > 6000)
            issues.Add("Dünya boyutu 1000–6000 aralığında olmalıdır.");
        if (string.IsNullOrWhiteSpace(settings.MapName))
            issues.Add("Harita adı boş olamaz.");

        var biomes = new[]
        {
            ("Çöl", settings.BiomeArid),
            ("Ilıman", settings.BiomeTemperate),
            ("Tundra", settings.BiomeTundra),
            ("Kar", settings.BiomeArctic),
            ("Jungle", settings.BiomeJungle)
        };
        ValidatePercentages(biomes, issues);
        if (biomes.Take(4).Sum(x => x.Item2) <= 0.0001)
            issues.Add("Ana biyomların tamamı sıfır olamaz; Çöl, Ilıman, Tundra veya Kar alanlarından en az birine değer verin.");

        var tiers = new[]
        {
            ("Tier 0", settings.Tier0),
            ("Tier 1", settings.Tier1),
            ("Tier 2", settings.Tier2)
        };
        ValidatePercentages(tiers, issues);
        if (tiers.Sum(x => x.Item2) <= 0.0001)
            issues.Add("Tier değerlerinin tamamı sıfır olamaz.");

        foreach (var rule in catalog.Where(x => !x.IsWorldFeature && x.State != RuleState.Optional))
        {
            if (rule.State == RuleState.Required && rule.Minimum < 1)
                issues.Add($"{rule.Name}: zorunlu monument için minimum adet en az 1 olmalıdır.");
            if (rule.State == RuleState.Required && rule.Maximum < Math.Max(1, rule.Minimum))
                issues.Add($"{rule.Name}: maksimum adet minimumdan küçük olamaz.");
        }

        ValidateTerrainDraft(settings.TerrainDraft, issues);
        return issues;
    }

    public static void ValidateOrThrow(AppSettings settings, IReadOnlyList<MonumentRule> catalog)
    {
        var issues = Validate(settings, catalog);
        if (issues.Count == 0) return;

        throw new InvalidOperationException(
            "Üretim başlatılmadı. Aşağıdaki ayarları düzeltin:" + Environment.NewLine +
            string.Join(Environment.NewLine, issues.Take(10).Select(x => "• " + x)) +
            (issues.Count > 10 ? $"{Environment.NewLine}• …ve {issues.Count - 10} sorun daha" : ""));
    }

    private static void ValidatePercentages(IEnumerable<(string Name, double Value)> values, List<string> issues)
    {
        foreach (var (name, value) in values)
            if (!double.IsFinite(value) || value is < 0 or > 100)
                issues.Add($"{name} değeri 0–100 aralığında olmalıdır.");
    }

    private static void ValidateTerrainDraft(TerrainDraft? draft, List<string> issues)
    {
        if (draft is null) return;
        if (!double.IsFinite(draft.BrushRadius) || draft.BrushRadius is < 0.005 or > 0.35)
            issues.Add("Arazi fırçası boyutu geçersiz (izin verilen normalize aralık 0,005–0,35).");

        var strokes = draft.Strokes ?? [];
        if (strokes.Count > MaximumTerrainSamples)
            issues.Add($"Arazi taslağı çok büyük: en fazla {MaximumTerrainSamples:N0} fırça noktası kullanılabilir.");

        if (strokes.Any(x =>
                string.IsNullOrWhiteSpace(x.StrokeId) ||
                !Enum.IsDefined(x.Kind) ||
                !double.IsFinite(x.X) || x.X is < 0 or > 1 ||
                !double.IsFinite(x.Y) || x.Y is < 0 or > 1 ||
                !double.IsFinite(x.Radius) || x.Radius is <= 0 or > 0.5 ||
                !double.IsFinite(x.Strength) || x.Strength is <= 0 or > 1))
            issues.Add("Arazi taslağında geçersiz koordinat, fırça türü, yarıçap veya güç bulundu.");
    }
}
