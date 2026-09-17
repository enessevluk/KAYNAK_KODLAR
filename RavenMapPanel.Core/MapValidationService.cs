namespace RavenMapPanel;

internal sealed record MapRuleValidation(
    MonumentRule Rule,
    int Count,
    int Required,
    bool IsValid,
    string Mode);

internal static class MapValidationService
{
    public static IReadOnlyList<MapRuleValidation> Evaluate(
        IEnumerable<string> markerCategories,
        IReadOnlyList<MonumentRule> catalog)
    {
        var counts = markerCategories
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

        return catalog
            .Where(x => x.State != RuleState.Optional)
            .Select(rule =>
            {
                var count = counts.GetValueOrDefault(rule.Id);
                var required = Math.Max(1, rule.Minimum);
                var maximum = Math.Max(required, rule.Maximum);
                var exactGodRock = rule.Id.Equals("god_rocks", StringComparison.OrdinalIgnoreCase);
                var ok = rule.State == RuleState.Required
                    ? (exactGodRock ? count == required : count >= required && count <= maximum)
                    : count == 0;
                var mode = rule.State == RuleState.Required
                    ? (exactGodRock
                        ? $"tam {required}"
                        : required == maximum ? $"tam {required}" : $"min {required} / max {maximum}")
                    : "yasaklı";
                return new MapRuleValidation(rule, count, required, ok, mode);
            })
            .ToList();
    }

    public static int CountIssues(IEnumerable<string> markerCategories, IReadOnlyList<MonumentRule> catalog) =>
        Evaluate(markerCategories, catalog).Count(x => !x.IsValid);
}
