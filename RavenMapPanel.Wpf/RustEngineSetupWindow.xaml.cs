using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using MessageBox = System.Windows.MessageBox;

namespace RavenMapPanel;

public partial class RustEngineSetupWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private readonly RustEngineService engine = new();
    private CancellationTokenSource? installCancellation;
    private bool installing;
    private readonly bool forceRepair;
    private readonly bool forceUpdate;
    private bool repairCompleted;
    private bool updateCompleted;
    private bool detailsVisible;

    public string SelectedRustServerPath { get; private set; }

    public RustEngineSetupWindow(string? currentRustServerPath = null, bool forceRepair = false, bool forceUpdate = false)
    {
        InitializeComponent();
        this.forceRepair = forceRepair;
        this.forceUpdate = forceUpdate;

        if (forceUpdate)
        {
            Title = "Raven Map Studio - Rust Güncellemesi";
            SetupEyebrow.Text = "RUST GÜNCELLEMESİ";
            SetupHeading.Text = "Harita motorunu güncelle";
            SetupIntro.Text = "Yeni Rust sürümü hazır. Raven yalnızca değişen dosyaları indirir; haritalarınız ve ayarlarınız korunur.";
            FooterHint.Text = "Güncelleme sırasında internet bağlantısı gereklidir.";
        }

        var current = string.IsNullOrWhiteSpace(currentRustServerPath)
            ? RustEngineService.DefaultRustServerPath
            : Path.GetFullPath(currentRustServerPath);

        // v3.7 and older used the developer machine path as the default.
        // A fresh/missing installation is migrated to the customer-safe Raven root.
        if (current.Equals(@"C:\RustMapServer\RustServer", StringComparison.OrdinalIgnoreCase) && !engine.Check(current).IsReady)
            current = RustEngineService.DefaultRustServerPath;

        SelectedRustServerPath = current;
        InstallPathBox.Text = SelectedRustServerPath;
        RefreshState();
        Closing += OnClosing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var enabled = 1;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }

    private void RefreshState()
    {
        var state = UpdateComponentState();

        if (state.IsReady)
        {
            ProgressTitle.Text = forceUpdate && !updateCompleted ? "Rust güncellemesi hazır" : "Harita motoru hazır";
            ProgressDetail.Text = forceUpdate && !updateCompleted
                ? "Güncelleme onaylandığında yalnızca değişen Rust sunucu dosyaları indirilecek."
                : "Bu Rust kurulumu Raven Map Studio ile harita üretmek için gerekli dosyalara sahip.";
            SetupProgress.IsIndeterminate = false;
            SetupProgress.Value = forceUpdate && !updateCompleted ? 0 : 100;
            ProgressPercentText.Text = forceUpdate && !updateCompleted ? "0%" : "100%";
            InstallButton.Content = forceUpdate && !updateCompleted
                ? "Şimdi güncelle"
                : forceRepair && !repairCompleted ? "Doğrula / onar" : "Devam et";
            LaterButton.Content = forceUpdate && !updateCompleted ? "Daha sonra" : "Kapat";
            InstallLog.Text = "✓ Sunucu dosyaları hazır\n✓ Harita üretim modülü hazır\n✓ İndirme aracı hazır";
        }
        else
        {
            ProgressTitle.Text = "Kurulum gerekli";
            ProgressDetail.Text = state.MissingFiles.Count == 0
                ? "Raven gerekli bileşenleri otomatik olarak hazırlayacak."
                : GetMissingSummary(state.MissingFiles);
            SetupProgress.IsIndeterminate = false;
            SetupProgress.Value = 0;
            ProgressPercentText.Text = "0%";
            InstallButton.Content = "Şimdi kur";
            InstallLog.Text = "";
        }
    }


    private RustEngineCheckResult UpdateComponentState()
    {
        var state = engine.Check(SelectedRustServerPath);
        SetState(RustDedicatedState, state.RustDedicatedExists, "Hazır", "İndirilecek");
        SetState(HarmonyState, state.HarmonyExists && state.RustHarmonyExists, "Hazır", "Kurulacak");
        SetState(SteamCmdState, File.Exists(Path.Combine(state.SteamCmdPath, "steamcmd.exe")), "Hazır", "Hazırlanacak");

        var free = engine.GetAvailableDiskSpaceBytes(SelectedRustServerPath);
        DiskSpaceText.Text = free.HasValue
            ? $"Boş alan: {free.Value / 1024d / 1024d / 1024d:0.0} GB"
            : "Kullanılabilir disk alanı okunamadı.";
        DiskSpaceText.Foreground = new SolidColorBrush(free switch
        {
            >= 20L * 1024 * 1024 * 1024 => System.Windows.Media.Color.FromRgb(0x5F, 0xD3, 0x9A),
            >= 15L * 1024 * 1024 * 1024 => System.Windows.Media.Color.FromRgb(0xF0, 0xB3, 0x55),
            null => System.Windows.Media.Color.FromRgb(0x83, 0x91, 0xA3),
            _ => System.Windows.Media.Color.FromRgb(0xF2, 0x6D, 0x78)
        });
        return state;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (engine.Check(SelectedRustServerPath).IsReady &&
            (!forceRepair || repairCompleted) &&
            (!forceUpdate || updateCompleted))
        {
            DialogResult = true;
            return;
        }

        if (installing) return;
        installing = true;
        installCancellation = new CancellationTokenSource();
        SetBusy(true);
        InstallLog.Clear();

        var progress = new Progress<RustEngineInstallProgress>(p =>
        {
            ProgressTitle.Text = p.Title;
            ProgressDetail.Text = p.Detail;
            SetupProgress.IsIndeterminate = p.IsIndeterminate;
            if (p.IsIndeterminate)
            {
                ProgressPercentText.Text = "Çalışıyor";
            }
            else
            {
                var percent = Math.Clamp(p.Percent, 0, 100);
                SetupProgress.Value = percent;
                ProgressPercentText.Text = $"{percent:0}%";
            }
            AppendLog(p.Detail);
        });

        try
        {
            var free = engine.GetAvailableDiskSpaceBytes(SelectedRustServerPath);
            if (free is < 15L * 1024 * 1024 * 1024)
            {
                var answer = MessageBox.Show(this,
                    "Seçilen diskte 15 GB'dan az boş alan görünüyor. Rust Dedicated kurulumu tamamlanamayabilir. Yine de devam etmek istiyor musunuz?",
                    "Düşük disk alanı", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }

            var state = forceUpdate
                ? await engine.UpdateAsync(SelectedRustServerPath, progress, installCancellation.Token)
                : await engine.InstallOrRepairAsync(SelectedRustServerPath, progress, installCancellation.Token, forceValidate: forceRepair);
            repairCompleted = forceRepair;
            updateCompleted = forceUpdate;
            SelectedRustServerPath = state.RustServerPath;
            InstallPathBox.Text = SelectedRustServerPath;
            RefreshState();
            FooterHint.Text = forceUpdate
                ? "Güncelleme tamamlandı. Raven harita üretmeye hazır."
                : "Kurulum tamamlandı. Raven harita üretmeye hazır.";
            LaterButton.Content = "Kapat";
            InstallButton.Content = "Devam et";
        }
        catch (OperationCanceledException)
        {
            ProgressTitle.Text = "Kurulum iptal edildi";
            ProgressDetail.Text = "İndirilen mevcut dosyalar korunur; kurulumu daha sonra devam ettirebilirsiniz.";
            SetupProgress.IsIndeterminate = false;
            ProgressPercentText.Text = "İptal edildi";
            AppendLog("Kurulum kullanıcı tarafından iptal edildi.");
        }
        catch (Exception ex)
        {
            ProgressTitle.Text = "Kurulum tamamlanamadı";
            ProgressDetail.Text = ex.Message;
            SetupProgress.IsIndeterminate = false;
            ProgressPercentText.Text = "Hata";
            AppendLog("HATA: " + ex.Message);
            ShowDetails();
            MessageBox.Show(this, ex.Message, "Harita motoru kurulamadı", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            installCancellation?.Dispose();
            installCancellation = null;
            installing = false;
            SetBusy(false);
            var finalState = UpdateComponentState();
            if (finalState.IsReady)
            {
                InstallButton.Content = forceUpdate && !updateCompleted
                    ? "Şimdi güncelle"
                    : forceRepair && !repairCompleted ? "Doğrula / onar" : "Devam et";
                LaterButton.Content = forceUpdate && !updateCompleted ? "Daha sonra" : "Kapat";
            }
        }
    }

    private void ChangeLocation_Click(object sender, RoutedEventArgs e)
    {
        if (installing) return;
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Rust Dedicated Server'ın kurulacağı klasörü seçin",
            SelectedPath = SelectedRustServerPath,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        SelectedRustServerPath = Path.GetFullPath(dialog.SelectedPath);
        repairCompleted = false;
        InstallPathBox.Text = SelectedRustServerPath;
        RefreshState();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (installing)
        {
            installCancellation?.Cancel();
            return;
        }
        DialogResult = forceUpdate && !updateCompleted
            ? false
            : engine.Check(SelectedRustServerPath).IsReady ? true : false;
    }

    private void SetBusy(bool busy)
    {
        ChangeLocationButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy;
        LaterButton.Content = busy ? "İptal" : "Daha sonra";
        LaterButton.IsEnabled = true;
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        detailsVisible = !detailsVisible;
        InstallLogPanel.Visibility = detailsVisible ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Content = detailsVisible ? "Ayrıntıları gizle" : "Ayrıntıları göster";
    }

    private void ShowDetails()
    {
        detailsVisible = true;
        InstallLogPanel.Visibility = Visibility.Visible;
        DetailsButton.Content = "Ayrıntıları gizle";
    }

    private static string GetMissingSummary(IReadOnlyCollection<string> missingFiles)
    {
        var parts = new List<string>();
        if (missingFiles.Any(file => file.Contains("RustDedicated", StringComparison.OrdinalIgnoreCase)))
            parts.Add("Rust sunucu dosyaları");
        if (missingFiles.Any(file => file.Contains("Harmony", StringComparison.OrdinalIgnoreCase)))
            parts.Add("harita üretim modülü");

        return parts.Count == 0
            ? "Eksik bileşenler otomatik olarak indirilecek ve doğrulanacak."
            : $"Hazırlanacak: {string.Join(" ve ", parts)}.";
    }

    private static void SetState(System.Windows.Controls.TextBlock target, bool ok, string okText, string missingText)
    {
        target.Text = ok ? "✓ " + okText : "• " + missingText;
        target.Foreground = new SolidColorBrush(ok
            ? System.Windows.Media.Color.FromRgb(0x5F, 0xD3, 0x9A)
            : System.Windows.Media.Color.FromRgb(0xF0, 0x9A, 0x48));
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (InstallLog.Text.EndsWith(text + Environment.NewLine, StringComparison.Ordinal)) return;
        InstallLog.AppendText(text.Trim() + Environment.NewLine);
        InstallLog.ScrollToEnd();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!installing) return;
        installCancellation?.Cancel();
        e.Cancel = true;
    }
}
