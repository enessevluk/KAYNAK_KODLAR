using System.Text;
using System.Windows;

namespace RavenMapPanel;

internal static class Program
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RavenMapPanel");
    private static readonly string LogPath = Path.Combine(LogDirectory, "startup.log");

    [STAThread]
    public static int Main()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(LogPath,
                $"[{DateTimeOffset.Now:O}] START version={typeof(Program).Assembly.GetName().Version} base={AppContext.BaseDirectory}{Environment.NewLine}",
                Encoding.UTF8);

            var app = new App();
            app.InitializeComponent();
            var result = app.Run();
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] EXIT code={result}{Environment.NewLine}", Encoding.UTF8);
            return result;
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath,
                    $"[{DateTimeOffset.Now:O}] FATAL{Environment.NewLine}{ex}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { }

            try
            {
                MessageBox.Show(
                    "Raven Map Panel başlatılamadı.\n\n" + ex.Message +
                    "\n\nTanı dosyası:\n" + LogPath,
                    "Raven Map Panel - Başlangıç Hatası",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { }
            return 1;
        }
    }
}
