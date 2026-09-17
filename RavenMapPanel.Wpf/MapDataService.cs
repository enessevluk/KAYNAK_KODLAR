using System.Windows.Media.Imaging;
using System.Text.Json;

namespace RavenMapPanel;

internal static class MapDataService
{
    public static MapDocument Load(string imagePath, string reports, int size, int seed, IReadOnlyList<MonumentRule> catalog)
    {
        var mainPath = Path.Combine(reports, $"RavenMapReport_{size}_{seed}.json");
        var worldPath = Path.Combine(reports, $"RavenWorldObjectReport_{size}_{seed}.json");
        var main = MapReportReader.Read(mainPath, size);
        var world = MapReportReader.Read(worldPath, size);
        var resolver = new MapEntryResolver(catalog);
        var resolved = resolver.Merge(main.Entries, world.Entries);

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(imagePath);
        bitmap.EndInit();
        bitmap.Freeze();

        var mapResolution = main.MapResolution > 0 ? main.MapResolution : world.MapResolution;
        var renderOffset = main.RenderOffset >= 0 ? main.RenderOffset : world.RenderOffset;
        if (mapResolution <= 0) { mapResolution = Math.Min(bitmap.PixelWidth, bitmap.PixelHeight); renderOffset = 0; }

        var doc = new MapDocument
        {
            ImagePath = imagePath,
            Image = bitmap,
            ImageWidth = bitmap.PixelWidth,
            ImageHeight = bitmap.PixelHeight,
            WorldSize = main.WorldSize > 0 ? main.WorldSize : (world.WorldSize > 0 ? world.WorldSize : size),
            MapResolution = mapResolution,
            RenderOffset = Math.Max(0, renderOffset),
            ReportFolder = reports,
            Seed = seed,
            TerrainSafety = ReadTerrainSafety(worldPath)
        };

        var byId = catalog.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in resolved)
        {
            if (!byId.TryGetValue(entry.Category, out var rule) || !rule.RenderOnMap) continue;
            var name = string.IsNullOrWhiteSpace(entry.Name) ? rule.Name : entry.Name;
            doc.Markers.Add(new MapMarker(entry.Category, name, entry.Source, entry.X, entry.Z, rule.Icon));
        }
        return doc;
    }

    private static TerrainSafetyView? ReadTerrainSafety(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (!json.RootElement.TryGetProperty("terrainSafety", out var node)) return null;

            static bool B(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
            static int I(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;
            static long L(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0;
            static string S(JsonElement e, string name) => e.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
            static double D(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed) ? parsed : 0d;

            var issues = new List<TerrainSafetyIssueView>();
            if (node.TryGetProperty("issues", out var issueNodes) && issueNodes.ValueKind == JsonValueKind.Array)
                foreach (var issue in issueNodes.EnumerateArray())
                    issues.Add(new TerrainSafetyIssueView(S(issue, "severity"), S(issue, "name"), D(issue, "x"), D(issue, "z"), D(issue, "terrainRange"), S(issue, "message")));

            return new TerrainSafetyView
            {
                Enabled = B(node, "enabled"), Mode = S(node, "mode"), BudgetMs = I(node, "budgetMs"),
                ElapsedMs = L(node, "elapsedMs"), Candidates = I(node, "candidates"), Checked = I(node, "checked"),
                Protected = I(node, "protected"), BudgetExceeded = B(node, "budgetExceeded"),
                HeightMapAvailable = B(node, "heightMapAvailable"), SafeAutoRepairEnabled = B(node, "safeAutoRepairEnabled"),
                AlphaMapAvailable = B(node, "alphaMapAvailable"), SlopeObservations = I(node, "slopeObservations"),
                RepairsApplied = I(node, "repairsApplied"), RepairCandidates = I(node, "repairCandidates"), Issues = issues
            };
        }
        catch
        {
            return null;
        }
    }
}
