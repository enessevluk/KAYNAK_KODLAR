using Xunit;

namespace RavenMapPanel;

public sealed class MapValidationTests
{
    [Fact]
    public void RequiredRule_RejectsCountAboveMaximum()
    {
        var rules = new List<MonumentRule>
        {
            new() { Id = "military_tunnel", Name = "Military Tunnel", State = RuleState.Required, Minimum = 1, Maximum = 1 }
        };

        var result = MapValidationService.Evaluate(new[] { "military_tunnel", "military_tunnel" }, rules).Single();

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Count);
        Assert.Equal("tam 1", result.Mode);
    }

    [Fact]
    public void RequiredRule_AcceptsCountInsideMinMaxRange()
    {
        var rules = new List<MonumentRule>
        {
            new() { Id = "harbor", Name = "Harbor", State = RuleState.Required, Minimum = 1, Maximum = 3 }
        };

        var result = MapValidationService.Evaluate(new[] { "harbor", "harbor" }, rules).Single();

        Assert.True(result.IsValid);
        Assert.Equal("min 1 / max 3", result.Mode);
    }

    [Fact]
    public void GodRock_RemainsExactCountRule()
    {
        var rules = new List<MonumentRule>
        {
            new() { Id = "god_rocks", Name = "God Rock", State = RuleState.Required, Minimum = 2, Maximum = 9 }
        };

        Assert.True(MapValidationService.Evaluate(new[] { "god_rocks", "god_rocks" }, rules).Single().IsValid);
        Assert.False(MapValidationService.Evaluate(new[] { "god_rocks", "god_rocks", "god_rocks" }, rules).Single().IsValid);
    }
}
