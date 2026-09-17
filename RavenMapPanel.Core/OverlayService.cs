using System.Drawing;
using System.Drawing.Drawing2D;

namespace RavenMapPanel;

internal sealed class OverlayService(List<MonumentRule> catalog)
{
    private readonly MapEntryResolver resolver = new(catalog);

    public (int Icons, int GodRocks) Render(string rawImage, string outputImage, string reportFolder, int size, int seed, double iconSize)
    {
        var mainPath = Path.Combine(reportFolder, $"RavenMapReport_{size}_{seed}.json");
        var worldPath = Path.Combine(reportFolder, $"RavenWorldObjectReport_{size}_{seed}.json");
        var main = MapReportReader.Read(mainPath, size);
        var world = MapReportReader.Read(worldPath, size);
        var entries = resolver.Merge(main.Entries, world.Entries);

        using var source = new Bitmap(rawImage);
        using var canvas = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(canvas);
        graphics.DrawImageUnscaled(source, 0, 0);
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var resolution = main.MapResolution > 0 ? main.MapResolution : world.MapResolution;
        var offset = main.RenderOffset >= 0 ? main.RenderOffset : world.RenderOffset;
        if (resolution <= 0) { resolution = Math.Min(source.Width, source.Height); offset = 0; }
        var reportWidth = main.ImageWidth > 0 ? main.ImageWidth : source.Width;
        var reportHeight = main.ImageHeight > 0 ? main.ImageHeight : source.Height;
        var iconBase = (float)(Math.Clamp(iconSize, 24, 72) * 1.5);
        var drawn = 0;

        foreach (var entry in entries)
        {
            var rule = catalog.FirstOrDefault(x => x.Id.Equals(entry.Category, StringComparison.OrdinalIgnoreCase));
            if (rule is null || !rule.RenderOnMap) continue;
            using var icon = AssetStore.LoadIcon(rule.Icon);
            if (icon is null) continue;

            var reportX = offset + ((entry.X + size / 2d) / size) * resolution;
            var reportY = offset + ((size / 2d - entry.Z) / size) * resolution;
            var px = (float)(reportX * source.Width / reportWidth);
            var py = (float)(reportY * source.Height / reportHeight);
            var visible = VisibleBounds(icon);
            var maxSide = Math.Max(visible.Width, visible.Height);
            var categoryScale = IsCompactIcon(entry.Category) ? .52f : 1f;
            var drawWidth = iconBase * categoryScale * visible.Width / (float)Math.Max(1, maxSide);
            var drawHeight = iconBase * categoryScale * visible.Height / (float)Math.Max(1, maxSide);
            graphics.DrawImage(icon,
                new RectangleF(px - drawWidth / 2, py - drawHeight / 2, drawWidth, drawHeight),
                visible, GraphicsUnit.Pixel);
            drawn++;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputImage)!);
        canvas.Save(outputImage, System.Drawing.Imaging.ImageFormat.Png);
        return (drawn, entries.Count(x => x.Category.Equals("god_rocks", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsCompactIcon(string id) => id == "caves" || id.StartsWith("cave_",StringComparison.OrdinalIgnoreCase) || id.StartsWith("power_sub_",StringComparison.OrdinalIgnoreCase) || id is "powerlines" or "power_substations" or "metro_entrances"
        or "icebergs" or "god_rocks" or "lakes" or "canyons" or "oases" or "swamps" or "water_wells" or "ruins";

    private static Rectangle VisibleBounds(Bitmap bitmap)
    {
        var minX = bitmap.Width; var minY = bitmap.Height; var maxX = -1; var maxY = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y).A <= 20) continue;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        return maxX < minX
            ? new Rectangle(0, 0, bitmap.Width, bitmap.Height)
            : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }
}
