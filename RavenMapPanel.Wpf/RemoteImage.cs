using System.IO;
using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RavenMapPanel;

/// <summary>
/// HTTPS üzerinden yalnızca sabit fotoğraf yükler. GIF dosyaları bilinçli olarak
/// reddedilir; ana kapakta ve duyurularda hareketli medya kullanılmaz.
/// </summary>
internal sealed class RemoteImage : Image
{
    private const int MaximumDownloadBytes = 25 * 1024 * 1024;
    private static readonly HttpClient MediaClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private int loadVersion;

    public void SetStaticSource(ImageSource source)
    {
        loadVersion++;
        Source = source;
    }

    public async Task<bool> LoadHttpsAsync(string url, int decodePixelHeight = 0)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var version = ++loadVersion;
        try
        {
            using var response = await MediaClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
                return false;

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0 || bytes.Length > MaximumDownloadBytes || version != loadVersion || IsGif(bytes))
                return false;

            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodePixelHeight > 0)
                bitmap.DecodePixelHeight = decodePixelHeight;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            if (version != loadVersion)
                return false;

            Source = bitmap;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGif(byte[] bytes) => bytes.Length >= 6
        && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F'
        && bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') && bytes[5] == (byte)'a';
}
