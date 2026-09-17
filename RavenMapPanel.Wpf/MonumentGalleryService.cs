using System.Windows.Media.Imaging;

namespace RavenMapPanel;

internal sealed record MonumentGalleryImage(BitmapImage Source);

internal static class MonumentGalleryService
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];
    public static string FolderFor(string monumentId) =>
        Path.Combine(SettingsStore.MonumentGalleryRoot, SettingsStore.SafeName(monumentId, "monument"));

    public static List<MonumentGalleryImage> Load(MonumentRule? rule)
    {
        var result = new List<MonumentGalleryImage>();
        if (rule is not null)
        {
            var folder = FolderFor(rule.Id);
            if (!Directory.Exists(folder))
                return result;
            foreach (var file in Directory.EnumerateFiles(folder)
                         .Where(x => Extensions.Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
                         .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
            {
                var image = LoadFile(file);
                if (image is not null) result.Add(new(image));
            }
        }
        return result;
    }

    private static BitmapImage? LoadFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 2560;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }
}
