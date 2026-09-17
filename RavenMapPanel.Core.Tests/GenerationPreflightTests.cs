using Xunit;

namespace RavenMapPanel;

public sealed class GenerationPreflightTests
{
    [Fact]
    public void ValidSettings_HaveNoIssues()
    {
        var settings = new AppSettings();

        var issues = GenerationPreflight.Validate(settings, []);

        Assert.Empty(issues);
    }

    [Fact]
    public void ZeroMainBiomes_AreRejectedBeforeGeneration()
    {
        var settings = new AppSettings
        {
            BiomeArid = 0,
            BiomeTemperate = 0,
            BiomeTundra = 0,
            BiomeArctic = 0
        };

        var issues = GenerationPreflight.Validate(settings, []);

        Assert.Contains(issues, x => x.Contains("tamamı sıfır", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidTerrainPoint_IsRejected()
    {
        var settings = new AppSettings
        {
            TerrainDraft = new TerrainDraft
            {
                Enabled = true,
                Strokes = [new TerrainStroke { X = 1.2, Y = 0.4, Radius = 0.08 }]
            }
        };

        var issues = GenerationPreflight.Validate(settings, []);

        Assert.Contains(issues, x => x.Contains("taslağında geçersiz", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RequiredRule_WithImpossibleRange_IsRejected()
    {
        var rule = new MonumentRule
        {
            Id = "outpost",
            Name = "Outpost",
            State = RuleState.Required,
            Minimum = 3,
            Maximum = 2
        };

        var issues = GenerationPreflight.Validate(new AppSettings(), [rule]);

        Assert.Contains(issues, x => x.Contains("maksimum", StringComparison.OrdinalIgnoreCase));
    }
}
