using System.Text.Json;

namespace RavenMapPanel;

internal sealed class PlacementPolicy
{
    public int MinDistance { get; set; } = 300;
    public int FlatRadius { get; set; } = 80;
    public int MaxHeightDelta { get; set; } = 18;
}

internal sealed class GenerationPolicyDocument
{
    public string Format { get; set; } = "raven-generation-policy-v1";
    public PlacementPolicy Defaults { get; set; } = new();
    public Dictionary<string, PlacementPolicy> Monuments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public PlacementPolicy For(string monumentId)
    {
        if (!Monuments.TryGetValue(monumentId, out var policy)) return Defaults;
        return new PlacementPolicy
        {
            MinDistance = policy.MinDistance > 0 ? policy.MinDistance : Defaults.MinDistance,
            FlatRadius = policy.FlatRadius > 0 ? policy.FlatRadius : Defaults.FlatRadius,
            MaxHeightDelta = policy.MaxHeightDelta > 0 ? policy.MaxHeightDelta : Defaults.MaxHeightDelta
        };
    }
}

internal static class GenerationPolicyStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string ExternalPath => Path.Combine(SettingsStore.ConfigRoot, "generation_policy.json");

    public static GenerationPolicyDocument Load()
    {
        EnsureExternalDefault();
        try
        {
            return JsonSerializer.Deserialize<GenerationPolicyDocument>(File.ReadAllText(ExternalPath), Options) ?? new();
        }
        catch
        {
            return JsonSerializer.Deserialize<GenerationPolicyDocument>(AssetStore.ReadText("config.generation_policy.json"), Options) ?? new();
        }
    }

    public static void EnsureExternalDefault()
    {
        if (File.Exists(ExternalPath)) return;
        Directory.CreateDirectory(SettingsStore.ConfigRoot);
        File.WriteAllText(ExternalPath, AssetStore.ReadText("config.generation_policy.json"));
    }
}
