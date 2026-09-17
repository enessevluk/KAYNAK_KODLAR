using System.Collections.Generic;
using System.Threading;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace RavenMapPanel;

public partial class App : System.Windows.Application
{
    private Mutex? singleInstanceMutex;
    public static LicenseService LicenseService { get; private set; } = null!;
    public static bool StartupLicenseValidated { get; private set; }

    internal static void SetLicenseGate(bool value) => StartupLicenseValidated = value;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        StartupLicenseValidated = false;

        singleInstanceMutex = new Mutex(true, @"Local\RavenMapPanel.Client.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Raven Map Panel zaten çalışıyor.", "Raven Map Panel", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ShowPendingUpdateError();

        try
        {
            LicenseService = new LicenseService();
            var bootstrap = await LicenseService.GetBootstrapAsync();
            var license = await LicenseService.ValidateCachedAsync(allowOffline: false);
            if (!license.IsValid)
            {
                // Sunucu erişilemiyor, lisans iptal/askıda/süresi dolmuş, cihaz uyuşmuyor vb.
                // bütün erişim durumları aynı merkezi LicenseWindow üzerinden açıklanır.
                // Böylece kullanıcı yalnız teknik bir MessageBox değil; neden + yapılabilecek işlem + destek bağlantısını görür.

                // Revoked/suspended/expired gibi durumlarda aktivasyonu hemen silmeyiz.
                // Böylece sonraki açılışta server aynı kesin durum/neden/aksiyon bilgisini tekrar verebilir.
                // Yalnız server tokenın gerçekten geçersiz olduğunu söylerse local aktivasyonu temizleriz.
                if (license.State == LicenseCheckState.Invalid &&
                    string.Equals(license.Issue?.Code, "ACTIVATION_INVALID", StringComparison.OrdinalIgnoreCase))
                    LicenseService.ClearActivation();

                var window = new LicenseWindow(LicenseService, license);
                if (window.ShowDialog() != true) { Shutdown(); return; }
                license = await LicenseService.ValidateCachedAsync(allowOffline: false);
                if (!license.IsValid) { Shutdown(); return; }
            }

            // Fail-closed gate: MainWindow is never allowed to exist until the current server
            // has explicitly validated this activation during this process startup.
            StartupLicenseValidated = true;
            await LicenseService.RefreshPrefabCatalogAsync();

            await ShowSubscriptionNoticeIfNeededAsync(license);

            UpdateInfo? update = null;
            try { update = await LicenseService.CheckUpdateAsync(); }
            catch (Exception ex)
            {
                // A valid 72-hour offline license may still open Raven if the update server cannot be reached.
                var current = await LicenseService.ValidateCachedAsync();
                if (!current.IsValid) { MessageBox.Show("Güncelleme/lisans sunucusuna ulaşılamadı.\n\n" + ex.Message, "Raven Map Panel", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(); return; }
            }

            if (update is { UpdateAvailable: true })
            {
                var updateWindow = new UpdateWindow(update, new UpdateService(LicenseService));
                updateWindow.ShowDialog();
                if (updateWindow.UpdateStarted) { Shutdown(); return; }

                // Raven releases are always blocking. Closing the updater never allows an old build to continue.
                MessageBox.Show(
                    "Yeni Raven Map Panel sürümü yüklenmeden uygulama kullanılamaz.",
                    "Güncelleme gerekli",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            var main = new MainWindow(bootstrap,license);
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
            WindowActivation.BringToFront(main);
        }
        catch (Exception ex)
        {
            StartupLicenseValidated = false;
            MessageBox.Show("Raven başlangıç doğrulaması tamamlanamadı.\n\n" + BuildExceptionMessage(ex), "Raven Map Panel", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private static async Task ShowSubscriptionNoticeIfNeededAsync(LicenseCheckResult license)
    {
        var notice = LicenseService.GetPendingSubscriptionNotice(license);
        if (notice is null) return;
        LicenseService.MarkSubscriptionNoticeShown(notice.Key);

        var text = notice.Message + "\n\nAboneliğinizi şimdi yenilemek ister misiniz?";
        var answer = MessageBox.Show(text, notice.Title, MessageBoxButton.YesNo,
            notice.IsGracePeriod ? MessageBoxImage.Warning : MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            var store = await LicenseService.GetStoreInfoAsync();
            if (store.Enabled && !string.IsNullOrWhiteSpace(store.BuyUrl))
                LicenseService.OpenStore(store.BuyUrl);
        }
        catch { }
    }

    private static string BuildExceptionMessage(Exception ex)
    {
        var messages = new List<string>();
        Exception? current = ex;
        while (current != null && messages.Count < 6)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message))
                messages.Add(current.Message);
            current = current.InnerException;
        }
        return string.Join("\n→ ", messages);
    }

    private static void ShowPendingUpdateError()
    {
        var path = Path.Combine(Path.GetTempPath(), "RavenMapPanel_UpdateError.txt");
        if (!File.Exists(path)) return;
        string detail;
        try
        {
            detail = File.ReadAllText(path).Trim();
            File.Delete(path);
        }
        catch { return; }

        const string targetPrefix = "TARGET_VERSION=";
        var current = typeof(App).Assembly.GetName().Version;
        if (detail.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var lineEnd = detail.IndexOfAny(['\r', '\n']);
            var targetText = (lineEnd < 0 ? detail[targetPrefix.Length..] : detail[targetPrefix.Length..lineEnd]).Trim();
            detail = lineEnd < 0 ? "" : detail[(lineEnd + 1)..].TrimStart('\r', '\n', ' ');
            if (Version.TryParse(targetText, out var target) && current is not null && current >= target)
                return;
        }
        else if (current is not null &&
                 (current >= new Version(1, 7, 11) || (current.Major == 1 && current.Minor == 0)))
        {
            // Eski 1.7.x güncelleyici hedef sürümü hata kaydına yazmıyordu. 1.0.0
            // temiz kurulumu açılabildiyse bu kaydın artık yeni updater ile ilgisi yoktur.
            // Yeni updater bütün gerçek hataları TARGET_VERSION ile kaydeder.
            return;
        }

        MessageBox.Show(
            "Güncelleme indirildi ancak Windows bazı Raven dosyalarını kullanımda tuttuğu için kurulamadı.\n\n" +
            "Raven Map Panel'i kapatıp birkaç saniye sonra yeniden deneyin. Sorun devam ederse yeni tam kurulum paketini kullanın.\n\n" +
            (string.IsNullOrWhiteSpace(detail) ? "" : "Ayrıntı: " + detail),
            "Güncelleme uygulanamadı",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
