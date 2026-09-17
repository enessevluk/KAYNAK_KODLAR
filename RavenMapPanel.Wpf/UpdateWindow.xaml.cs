using System.ComponentModel;
using System.Windows;

namespace RavenMapPanel;

public partial class UpdateWindow : Window
{
    private readonly UpdateInfo info;
    private readonly UpdateService updater;
    private bool allowClose;

    public bool UpdateStarted { get; private set; }

    public UpdateWindow(UpdateInfo info, UpdateService updater)
    {
        this.info = info;
        this.updater = updater;
        InitializeComponent();

        VersionText.Text = $"Mevcut {info.CurrentVersion}   →   Yeni {info.LatestVersion}";
        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes)
            ? "Bu sürüm için değişiklik notu girilmemiş."
            : info.Notes;
        DownloadProgress.Visibility = Visibility.Collapsed;
        StatusText.Text = "Güncelleme yüklenmeye hazır.";
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Güncelleme indiriliyor...";

        try
        {
            var progress = new Progress<double>(p =>
            {
                DownloadProgress.Value = p;
                StatusText.Text = $"Güncelleme indiriliyor... %{p:0}";
            });

            await updater.DownloadVerifyAndStageAsync(info, progress);
            StatusText.Text = "Güncelleme doğrulandı. Raven yeniden başlatılıyor...";
            UpdateStarted = true;
            allowClose = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Güncelleme başarısız: " + ex.Message;
            UpdateButton.IsEnabled = true;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // The title-bar X may close the updater, but it must never let the old Raven build continue.
        // App.xaml.cs shuts the application down when UpdateStarted is false.
        if (UpdateStarted || allowClose)
            return;

        allowClose = true;
    }
}
