using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Image = System.Windows.Controls.Image;
using RavenMapPanel.Views;

namespace RavenMapPanel;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private readonly MainWindowViewModel viewModel;
    private AppSettings settings { get => viewModel.Settings; set => viewModel.Settings = value; }
    private List<MonumentRule> catalog => viewModel.Catalog;
    private ObservableCollection<MonumentRuleViewModel> MonumentItems => viewModel.MonumentItems;
    private ICollectionView MonumentView => viewModel.MonumentView;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer heroTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly string[] heroBackgrounds = ["arkaplan-2.jpg", "arkaplan-3.jpg", "arkaplan-4.jpg", "arkaplan-5.jpg"];
    private IReadOnlyList<string> heroMediaUrls = [];
    private int heroIndex = 3;
    private int heroLoadVersion;
    private CancellationTokenSource? cancellation { get => viewModel.GenerationCancellation; set => viewModel.GenerationCancellation = value; }
    private GenerationResult? result { get => viewModel.Result; set => viewModel.Result = value; }
    private string searchText { get => viewModel.SearchText; set => viewModel.SearchText = value; }
    private string selectedCategory { get => viewModel.SelectedCategory; set => viewModel.SelectedCategory = value; }
    private string selectedStateFilter { get => viewModel.SelectedStateFilter; set => viewModel.SelectedStateFilter = value; }
    private MapMarker? selectedMapMarker { get => viewModel.SelectedMapMarker; set => viewModel.SelectedMapMarker = value; }
    private List<MonumentGalleryImage> markerGallery = [];
    private int markerGalleryIndex;
    private ObservableCollection<GenerationHistoryEntry> GenerationHistoryItems => viewModel.GenerationHistoryItems;
    private ObservableCollection<MapComparisonRow> ComparisonItems => viewModel.ComparisonItems;
    private readonly ProjectService projectService = new();
    private readonly RustEngineService rustEngineService = new();
    private bool initialEngineCheckShown;
    private readonly DispatcherTimer rustUpdateTimer = new() { Interval = TimeSpan.FromHours(4) };
    private bool rustUpdateCheckBusy;
    private bool rustUpdateAvailable;
    private string installedRustBuildId = "";
    private string currentRustBuildId = "";
    private DateTimeOffset lastRustUpdateCheckUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastRustUpdatePromptUtc = DateTimeOffset.MinValue;
    private readonly DispatcherTimer licenseHeartbeatTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool licenseHeartbeatBusy;
    private bool licenseAccessLost;
    private bool suppressProjectChangeTracking;
    private bool projectDirtyTrackingWired;
    private bool projectDirty;
    private bool previewExpanded;
    private string savedProjectFingerprint = "";
    private BootstrapInfo dashboardBootstrap;
    private LicenseCheckResult dashboardLicense;
    private bool maintenanceActive;

    public MainWindow(BootstrapInfo bootstrap,LicenseCheckResult license)
    {
        if (!App.StartupLicenseValidated)
            throw new InvalidOperationException("Raven ana paneli, çevrimiçi lisans doğrulaması tamamlanmadan açılamaz.");

        dashboardBootstrap=bootstrap;
        dashboardLicense=license;
        viewModel = new MainWindowViewModel();
        InitializeComponent();
        var displayVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.7.0";
        VersionTextBlock.Text = $"V{displayVersion} · {BuildStamp.Id}";
        DataContext = viewModel;
        InitializeTerrainDraftEditor();
        foreach (var item in MonumentItems)
            item.PropertyChanged += MonumentItem_PropertyChanged;
        CategoryBox.Items.Add("Tümü"); foreach (var c in catalog.Select(x => x.Category).Distinct().OrderBy(x => x)) CategoryBox.Items.Add(c); CategoryBox.SelectedIndex = 0;
        foreach (var state in new[] { "Tümü", "Olsun", "Olmasın", "İsteğe bağlı" }) StateFilterBox.Items.Add(state);
        StateFilterBox.SelectedIndex = 0;
        HistoryList.ItemsSource = GenerationHistoryItems;
        CompareABox.ItemsSource = GenerationHistoryItems;
        CompareBBox.ItemsSource = GenerationHistoryItems;
        ComparisonList.ItemsSource = ComparisonItems;
        MapView.ZoomChanged += value => ZoomLabel.Text = $"{value:0}%";
        MapView.MarkerSelected += OnMapMarkerSelected;
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            SyncUiToSettings();
            SettingsStore.Save(settings);
            RefreshProjectDirtyState();
        };
        heroTimer.Tick += (_, _) => StepHero(1);
        heroTimer.Start();
        UpdateHeroBackground();
        ApplyDashboardStatus(bootstrap,license);
        LoadSettingsIntoUi();
        MarkProjectStateSaved();
        WireProjectDirtyTracking();
        RefreshHistory();
        Navigate("Dashboard");
        ContentRendered += MainWindow_ContentRendered;
        rustUpdateTimer.Tick += async (_, _) => await CheckRustEngineUpdateAsync(offerUpdate: true);
        rustUpdateTimer.Start();
        licenseHeartbeatTimer.Tick += async (_, _) => await ValidateLiveLicenseAsync();
        licenseHeartbeatTimer.Start();
        Activated += async (_, _) => await ValidateLiveLicenseAsync();
        Closing += MainWindow_Closing;
    }

    private async Task ValidateLiveLicenseAsync()
    {
        if (licenseHeartbeatBusy || licenseAccessLost || !IsLoaded) return;
        licenseHeartbeatBusy = true;
        try
        {
            var check = await App.LicenseService.RequireOnlineEntitlementAsync();
            if (check.IsValid)
            {
                dashboardLicense=check;
                var bootstrap=await App.LicenseService.GetBootstrapAsync();
                dashboardBootstrap=bootstrap;
                ApplyDashboardStatus(bootstrap,check);
                if (TopStatus.Text == "Lisans sunucusuna ulaşılamıyor")
                    TopStatus.Text = "Hazır";
                return;
            }

            if (check.State == LicenseCheckState.ServerUnavailable)
            {
                // An already-open UI may stay visible during a short network outage, but generation
                // remains fail-closed because GenerateAsync performs the same strict online check.
                TopStatus.Text = "Lisans sunucusuna ulaşılamıyor";
                return;
            }

            await RevokeOpenSessionAsync(check);
        }
        catch (Exception ex)
        {
            TopStatus.Text = "Lisans kontrolü başarısız";
            Debug.WriteLine("Raven live license check failed: " + ex);
        }
        finally
        {
            licenseHeartbeatBusy = false;
        }
    }

    private void ApplyDashboardStatus(BootstrapInfo bootstrap,LicenseCheckResult license)
    {
        var hasAnnouncement=!string.IsNullOrWhiteSpace(bootstrap.AnnouncementId)
            && (!string.IsNullOrWhiteSpace(bootstrap.AnnouncementTitle) || !string.IsNullOrWhiteSpace(bootstrap.AnnouncementMessage));
        DashboardAnnouncementTitle.Text=hasAnnouncement
            ? (string.IsNullOrWhiteSpace(bootstrap.AnnouncementTitle)?"Duyuru":bootstrap.AnnouncementTitle)
            : "Raven Map Studio'ya Hoş Geldiniz";
        DashboardAnnouncementMessage.Text=hasAnnouncement
            ? bootstrap.AnnouncementMessage
            : "Harita üretimiyle ilgili duyurular, yenilikler ve bakım bilgileri burada açık ve düzenli biçimde gösterilir.";

        var level=(bootstrap.AnnouncementLevel??"INFO").ToUpperInvariant();
        ApplyAnnouncementAppearance(level,hasAnnouncement);
        DashboardCriticalBar.Visibility=level=="CRITICAL"&&hasAnnouncement?Visibility.Visible:Visibility.Collapsed;
        DashboardCriticalText.Text=DashboardAnnouncementTitle.Text+" — "+DashboardAnnouncementMessage.Text;
        DashboardAnnouncementPeriod.Text=FormatAnnouncementPeriod(bootstrap);

        SetHeroMediaUrls(bootstrap.HomeMediaUrls);
        UpdateHeroBackground();
        UpdateAnnouncementImages(bootstrap.AnnouncementImageUrl, hasAnnouncement);

        var premium=license.Plan==EntitlementPlan.Premium;
        var planName=premium?"PREMIUM":"STANDARD";
        var licenseId=string.IsNullOrWhiteSpace(license.SteamId)?"—":license.SteamId;
        BottomAccountStatus.Text=$"{planName} PAKET · Lisans: {licenseId}";
        BottomAccountStatus.Foreground=new SolidColorBrush(premium
            ? System.Windows.Media.Color.FromRgb(0xFF,0xC8,0x68)
            : System.Windows.Media.Color.FromRgb(0x77,0xD9,0xBE));

        maintenanceActive=bootstrap.Maintenance;
        if (maintenanceActive)
        {
            DashboardCriticalBar.Visibility=Visibility.Visible;
            DashboardCriticalText.Text="Raven bakımda — "+(string.IsNullOrWhiteSpace(bootstrap.MaintenanceMessage)?"Harita üretimi geçici olarak durduruldu.":bootstrap.MaintenanceMessage);
            GenerateButton.IsEnabled=false;
            TopStatus.Text="Bakım modu";
        }
        else if (cancellation is null) GenerateButton.IsEnabled=true;
    }

    private static string FormatAnnouncementPeriod(BootstrapInfo bootstrap)
    {
        if (string.IsNullOrWhiteSpace(bootstrap.AnnouncementId)) return "Raven'dan güncel bilgiler";
        if (bootstrap.AnnouncementStartsUtc is null && bootstrap.AnnouncementEndsUtc is null) return "Aktif duyuru · Süresiz";
        var start=bootstrap.AnnouncementStartsUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm")??"Şimdi";
        var end=bootstrap.AnnouncementEndsUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm")??"Süresiz";
        return $"Yayın: {start} → {end}";
    }

    private void ApplyAnnouncementAppearance(string level, bool hasAnnouncement)
    {
        var (label,accent,badge)=(hasAnnouncement?level:"INFO") switch
        {
            "UPDATE" => ("GÜNCELLEME",Color.FromRgb(0xE7,0x72,0x3E),Color.FromRgb(0x43,0x29,0x20)),
            "WARNING" => ("UYARI",Color.FromRgb(0xD8,0xA9,0x32),Color.FromRgb(0x3D,0x33,0x18)),
            "CRITICAL" => ("ÖNEMLİ",Color.FromRgb(0xE0,0x5A,0x62),Color.FromRgb(0x43,0x20,0x24)),
            _ => (hasAnnouncement?"BİLGİ":"HOŞ GELDİNİZ",Color.FromRgb(0x42,0xA6,0xC9),Color.FromRgb(0x1C,0x36,0x43))
        };
        DashboardAnnouncementLevelText.Text=label;
        DashboardAnnouncementAccent.Background=new SolidColorBrush(accent);
        DashboardAnnouncementBadge.Background=new SolidColorBrush(badge);
        DashboardAnnouncementBadge.BorderBrush=new SolidColorBrush(accent);
        DashboardAnnouncementLevelText.Foreground=new SolidColorBrush(accent);
    }

    private void UpdateAnnouncementImages(string? rawUrls, bool hasAnnouncement)
    {
        DashboardAnnouncementImagesPanel.Children.Clear();
        DashboardAnnouncementImagesScroller.Visibility = Visibility.Collapsed;
        if (!hasAnnouncement || string.IsNullOrWhiteSpace(rawUrls)) return;

        var urls = ParseMediaUrls(rawUrls);

        foreach (var url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                continue;
            try
            {
                var image = new RemoteImage
                {
                    Width = 220,
                    Height = 110,
                    Stretch = Stretch.UniformToFill,
                    ToolTip = url
                };
                var border = new Border
                {
                    Width = 222,
                    Height = 112,
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x31, 0x42, 0x51)),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x12, 0x19)),
                    Margin = new Thickness(0, 0, 10, 2),
                    ClipToBounds = true,
                    Child = image
                };
                DashboardAnnouncementImagesPanel.Children.Add(border);
                _ = image.LoadHttpsAsync(uri.ToString(),220);
            }
            catch
            {
                // Hatalı/ulaşılamayan bir görsel tüm duyuruyu bozmaz.
            }
        }

        if (DashboardAnnouncementImagesPanel.Children.Count > 0)
            DashboardAnnouncementImagesScroller.Visibility = Visibility.Visible;
    }

    private static IReadOnlyList<string> ParseMediaUrls(string? rawUrls)
    {
        if (string.IsNullOrWhiteSpace(rawUrls)) return [];
        return rawUrls.Replace("\r", "", StringComparison.Ordinal)
            .Split(['\n', ','],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
            .Where(url => Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.Scheme==Uri.UriSchemeHttps)
            .Where(url => !Uri.TryCreate(url,UriKind.Absolute,out var uri)||!uri.AbsolutePath.EndsWith(".gif",StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
    }

    private void SetHeroMediaUrls(string? rawUrls)
    {
        var incoming=ParseMediaUrls(rawUrls);
        if (heroMediaUrls.SequenceEqual(incoming,StringComparer.OrdinalIgnoreCase)) return;
        heroMediaUrls=incoming;
        heroIndex=0;
    }

    private async Task RevokeOpenSessionAsync(LicenseCheckResult result)
    {
        if (licenseAccessLost) return;
        licenseAccessLost = true;
        licenseHeartbeatTimer.Stop();
        App.SetLicenseGate(false);
        cancellation?.Cancel();

        // Askıya alma / iptal / süre bitimi bilgisini silmeyiz. Client tekrar açıldığında
        // server aynı kesin nedeni ve aksiyonları gösterebilsin. Sadece token gerçekten
        // geçersizse local aktivasyonu temizlemek güvenlidir.
        if (string.Equals(result.Issue?.Code, "ACTIVATION_INVALID", StringComparison.OrdinalIgnoreCase))
            App.LicenseService.ClearActivation();

        await Dispatcher.InvokeAsync(() =>
        {
            var accessWindow = new LicenseWindow(App.LicenseService, result) { Owner = this };
            var reactivated = accessWindow.ShowDialog() == true;
            if (reactivated)
            {
                MessageBox.Show(this,
                    "Raven erişiminiz yeniden doğrulandı. Güvenli bir oturum için Raven şimdi kapanacak; uygulamayı tekrar açabilirsiniz.",
                    "Raven erişimi yenilendi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            System.Windows.Application.Current.Shutdown();
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var enabled = 1;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }

    private async void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (initialEngineCheckShown) return;
        initialEngineCheckShown = true;
        if (!string.IsNullOrWhiteSpace(SettingsStore.LastLoadWarning))
            MessageBox.Show(this, SettingsStore.LastLoadWarning, "Raven ayar kurtarma", MessageBoxButton.OK, MessageBoxImage.Warning);
        var state = rustEngineService.Check(settings.RustServerPath);
        UpdateRustEngineStatus(state);
        if (!state.IsReady)
            ShowRustEngineSetup(false);
        else
            await CheckRustEngineUpdateAsync(offerUpdate: true, force: true);
    }

    private async Task CheckRustEngineUpdateAsync(bool offerUpdate, bool force = false)
    {
        if (rustUpdateCheckBusy || cancellation is not null || !IsLoaded) return;
        if (!rustEngineService.Check(settings.RustServerPath).IsReady) return;
        if (!force && DateTimeOffset.UtcNow - lastRustUpdateCheckUtc < TimeSpan.FromMinutes(30))
        {
            if (offerUpdate && rustUpdateAvailable)
                OfferRustEngineUpdate();
            return;
        }

        rustUpdateCheckBusy = true;
        var previousTopStatus = TopStatus.Text;
        if (string.Equals(previousTopStatus, "Hazır", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(previousTopStatus, "Harita motoru hazır", StringComparison.OrdinalIgnoreCase))
            TopStatus.Text = "Rust güncellemesi denetleniyor";

        try
        {
            var status = await rustEngineService.CheckForUpdateAsync(settings.RustServerPath, CancellationToken.None);
            lastRustUpdateCheckUtc = DateTimeOffset.UtcNow;
            if (!status.CheckSucceeded) return;

            installedRustBuildId = status.InstalledBuildId;
            currentRustBuildId = status.CurrentBuildId;
            rustUpdateAvailable = status.UpdateAvailable;
            UpdateRustEngineStatus(rustEngineService.Check(settings.RustServerPath));
            if (offerUpdate && rustUpdateAvailable)
                OfferRustEngineUpdate();
        }
        finally
        {
            rustUpdateCheckBusy = false;
            if (TopStatus.Text == "Rust güncellemesi denetleniyor")
                TopStatus.Text = previousTopStatus;
        }
    }

    private void OfferRustEngineUpdate()
    {
        if (!rustUpdateAvailable || cancellation is not null) return;
        if (DateTimeOffset.UtcNow - lastRustUpdatePromptUtc < TimeSpan.FromHours(4)) return;
        lastRustUpdatePromptUtc = DateTimeOffset.UtcNow;

        var answer = MessageBox.Show(this,
            $"Rust Dedicated Server için yeni bir güncelleme bulundu.\n\n" +
            $"Kurulu sürüm: {installedRustBuildId}\n" +
            $"Güncel sürüm: {currentRustBuildId}\n\n" +
            "Şimdi güncellemek ister misiniz? Yalnızca değişen dosyalar indirilir; haritalarınız ve ayarlarınız korunur.",
            "Rust sunucu güncellemesi", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes)
            ShowRustEngineSetup(false, forceUpdate: true);
    }

    private bool EnsureRustEngineReady(bool requiredForGeneration)
    {
        SyncUiToSettings();
        var state = rustEngineService.Check(settings.RustServerPath);
        UpdateRustEngineStatus(state);
        if (state.IsReady) return true;
        return ShowRustEngineSetup(requiredForGeneration);
    }

    private bool ShowRustEngineSetup(bool requiredForGeneration, bool forceRepair = false, bool forceUpdate = false)
    {
        if (forceUpdate && cancellation is not null)
        {
            MessageBox.Show(this, "Harita üretimi devam ederken Rust güncellenemez. Üretim tamamlandıktan sonra yeniden deneyin.",
                "Güncelleme bekliyor", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var setup = new RustEngineSetupWindow(settings.RustServerPath, forceRepair, forceUpdate) { Owner = this };
        var accepted = setup.ShowDialog() == true;
        if (accepted)
        {
            settings.RustServerPath = setup.SelectedRustServerPath;
            RustPathBox.Text = settings.RustServerPath;
            SettingsStore.Save(settings);
            var state = rustEngineService.Check(settings.RustServerPath);
            UpdateRustEngineStatus(state);
            if (state.IsReady)
            {
                if (forceUpdate)
                {
                    rustUpdateAvailable = false;
                    installedRustBuildId = currentRustBuildId;
                    lastRustUpdateCheckUtc = DateTimeOffset.MinValue;
                }
                TopStatus.Text = "Harita motoru hazır";
                return true;
            }
        }

        UpdateRustEngineStatus(rustEngineService.Check(settings.RustServerPath));
        if (requiredForGeneration)
        {
            MessageBox.Show(this,
                "Harita üretilebilmesi için Rust Dedicated harita motorunun kurulması gerekir. Ayarlar > Rust Sunucu Klasörü bölümünden kurulumu yeniden başlatabilirsiniz.",
                "Harita motoru gerekli", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return false;
    }

    private void UpdateRustEngineStatus(RustEngineCheckResult state)
    {
        if (RustEngineStatusText is null || RustEngineDetailText is null || InstallRustEngineButton is null) return;
        if (state.IsReady)
        {
            if (rustUpdateAvailable)
            {
                RustEngineStatusText.Text = "↑ Rust güncellemesi hazır";
                RustEngineStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0x9A, 0x48));
                RustEngineDetailText.Text = $"Kurulu sürüm {installedRustBuildId} • Güncel sürüm {currentRustBuildId}";
                InstallRustEngineButton.Content = "Şimdi Güncelle";
            }
            else
            {
                RustEngineStatusText.Text = "✓ Harita motoru hazır";
                RustEngineStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5F, 0xD3, 0x9A));
                RustEngineDetailText.Text = string.IsNullOrWhiteSpace(installedRustBuildId)
                    ? "Rust Dedicated ve Harmony çalışma dosyaları doğrulandı."
                    : $"Rust Dedicated güncel • Build {installedRustBuildId}";
                InstallRustEngineButton.Content = "Doğrula / Onar";
            }
        }
        else
        {
            RustEngineStatusText.Text = "• Kurulum gerekli";
            RustEngineStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0x9A, 0x48));
            RustEngineDetailText.Text = state.MissingFiles.Count == 0 ? "Harita motoru henüz kurulmadı." : "Eksik: " + string.Join(", ", state.MissingFiles);
            InstallRustEngineButton.Content = "Harita Motorunu Kur";
        }
    }
}
