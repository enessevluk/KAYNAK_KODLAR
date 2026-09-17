using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

namespace RavenMapPanel;

public partial class MainWindow : Window
{
    private async void Generate_Click(object sender, RoutedEventArgs e) => await GenerateAsync();
    private async Task GenerateAsync()
    {
        if (maintenanceActive)
        {
            MessageBox.Show(this,"Raven bakım modundayken harita üretimi başlatılamaz.","Raven bakımda",MessageBoxButton.OK,MessageBoxImage.Information);
            return;
        }
        if (!int.TryParse(SeedBox.Text, out var seed) || seed <= 0)
        {
            MessageBox.Show(this, "Seed sıfırdan büyük bir tam sayı olmalıdır.", "Üretim ayarı geçersiz", MessageBoxButton.OK, MessageBoxImage.Warning);
            SeedBox.Focus();
            return;
        }
        if (!int.TryParse(SizeBox.Text, out var worldSize) || worldSize is < 1000 or > 6000)
        {
            MessageBox.Show(this, "Dünya boyutu 1000–6000 aralığında bir tam sayı olmalıdır.", "Üretim ayarı geçersiz", MessageBoxButton.OK, MessageBoxImage.Warning);
            SizeBox.Focus();
            return;
        }
        SyncUiToSettings();
        var licenseCheck = await App.LicenseService.RequireOnlineEntitlementAsync();
        if (!licenseCheck.IsValid)
        {
            if (licenseCheck.State != LicenseCheckState.ServerUnavailable)
            {
                await RevokeOpenSessionAsync(licenseCheck);
                return;
            }
            MessageBox.Show(this, licenseCheck.Message, "Raven lisans doğrulaması", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await App.LicenseService.RefreshPrefabCatalogAsync();
        viewModel.RefreshCustomPrefabVariants();
        await CheckRustEngineUpdateAsync(offerUpdate: true);
        if (!EnsureRustEngineReady(true)) return;
        if (string.IsNullOrWhiteSpace(settings.CurrentProjectPath))
            SaveProject(SettingsStore.DefaultProjectPath(settings.MapName));

        SettingsStore.Save(settings);
        cancellation = new CancellationTokenSource();
        GenerateButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ExportPngButton.IsEnabled = ExportMapButton.IsEnabled = false;
        GenerationProgress.IsIndeterminate = false;
        GenerationProgress.Minimum = 0;
        GenerationProgress.Maximum = 100;
        GenerationProgress.Value = 0;
        SideProgressTitle.Text = "Üretim kuyruğu hazırlanıyor";
        MapCanvasStatus.Text = "Üretim devam ediyor";
        TopStatus.Text = "Üretim sürüyor";
        StudioLog.Text = "";
        FullLog.Clear();
        lastStudioLogText = "";
        studioRecentLogs.Clear();
        collapsedSessionLogs.Clear();
        ResetProductionSummary();
        AppendFriendlyLog("HAZIRLIK", $"Yeni üretim başladı — Seed {settings.Seed}, boyut {settings.WorldSize}.", true);
        SetPreviewExpanded(false);
        Navigate("Studio");

        var count = Math.Clamp(settings.GenerationCount, 1, 7);
        var completed = 0;
        try
        {
            var queue = new GenerationQueueService(catalog, projectService,App.LicenseService);
            queue.Log += message => Dispatcher.Invoke(() => AppendLog(message));
            var queueResult = await queue.GenerateAsync(settings, cancellation.Token, progress =>
            {
                if (progress.Stage == GenerationQueueStage.Starting)
                {
                    SideProgressTitle.Text = $"Harita {progress.Number}/{progress.Count} üretiliyor";
                    SideProgressDetail.Text = $"Seed {progress.Seed} · {settings.WorldSize} metre";
                    TopStatus.Text = $"Kuyruk {progress.Number}/{progress.Count}";
                    GenerationProgress.Value = (progress.Number - 1) * 100d / progress.Count;
                    return;
                }

                result = progress.Result!;
                LoadMap(result.RawImagePath, result.ReportFolder, result.WorldSize, result.Seed);
                UpdateProductionSummary(MapView.Document!, appendToLogs: true, progress.Number, progress.Count);
                RecordGenerationHistory(result);
                completed = progress.Number;
                GenerationProgress.Value = completed * 100d / progress.Count;
            });

            settings.Seed = queueResult.Seeds[^1];
            SeedBox.Text = settings.Seed.ToString();
            SettingsStore.Save(settings);
            result = queueResult.Results.LastOrDefault();
            var validation = CurrentValidationResults();
            var failedRules = validation.Count(x => !x.IsValid);
            SideProgressTitle.Text = failedRules == 0
                ? (count == 1 ? "Üretim tamamlandı · kurallar başarılı" : $"{completed} haritanın tamamı üretildi")
                : (count == 1 ? $"Üretim tamamlandı · {failedRules} kural kontrol edilmeli" : $"Kuyruk tamamlandı · son haritada {failedRules} kural sorunu");
            SideProgressDetail.Text = CurrentValidationStatusDetail();
            MapCanvasStatus.Text = failedRules == 0 ? "Kurallar uygulandı" : $"{failedRules} kural uygulanmadı";
            ShowStudioValidationSummary();
            TopStatus.Text = failedRules == 0 ? (count == 1 ? "Harita hazır" : "Kuyruk tamamlandı") : $"Harita hazır · {failedRules} kural sorunu";
            GenerationProgress.Value = 100;
            ExportPngButton.IsEnabled = ExportMapButton.IsEnabled = result is not null;
        }
        catch (OperationCanceledException)
        {
            SideProgressTitle.Text = $"Kuyruk iptal edildi ({completed}/{count} tamamlandı)";
            SideProgressDetail.Text = "Tamamlanan haritalar korunmuştur.";
            TopStatus.Text = "İptal edildi";
        }
        catch (Exception ex)
        {
            AppendLog("HATA: " + ex.Message);
            SideProgressTitle.Text = $"Kuyruk durdu ({completed}/{count} tamamlandı)";
            SideProgressDetail.Text = ex.Message;
            TopStatus.Text = "Hata";
            MessageBox.Show(this, $"{completed}/{count} harita tamamlandı.\n\n{ex.Message}", "Üretim kuyruğu hatası", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            GenerationProgress.IsIndeterminate = false;
            GenerateButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            cancellation?.Dispose();
            cancellation = null;
        }
    }

    private void LoadMap(string image, string reports, int size, int seed)
    {
        var doc = MapDataService.Load(image, reports, size, seed, catalog); MapView.Document = doc; MarkerDetailsCard.Visibility = Visibility.Collapsed; MapCanvasStatus.Text = $"Harita hazır · {size} metre"; result ??= new("", "", Path.GetDirectoryName(image) ?? "", doc.Markers.Count, doc.Markers.Count(x => x.Category == "god_rocks"), image, reports, size, seed); ExportPngButton.IsEnabled = true; if (!string.IsNullOrWhiteSpace(result.MapPath) && File.Exists(result.MapPath)) ExportMapButton.IsEnabled = true; ValidateMap(); ShowStudioValidationSummary(); SetPreviewExpanded(false);
    }
    private void LoadLatestMap_Click(object sender, RoutedEventArgs e)
    {
        SyncUiToSettings();

        // Başarılı üretimden sonra Rust maps/mapimages çalışma dosyaları bilerek temizlenir.
        // Bu nedenle son haritayı çalışma klasöründen değil kalıcı GenerationHistory kaydından aç.
        var history = GenerationHistoryStore.LoadExisting()
            .Where(x => x.WorldSize == settings.WorldSize && x.Seed == settings.Seed)
            .OrderByDescending(x => x.GeneratedAt)
            .FirstOrDefault(x => File.Exists(x.RawImagePath) && Directory.Exists(x.ReportFolder));

        if (history is not null)
        {
            result = new GenerationResult(history.MapPath, history.ImagePath, history.OutputFolder,
                history.IconCount, history.GodRockCount, history.RawImagePath, history.ReportFolder,
                history.WorldSize, history.Seed, history.BackupPath);
            LoadMap(history.RawImagePath, history.ReportFolder, history.WorldSize, history.Seed);
            Navigate("Studio");
            TopStatus.Text = "Son üretilen harita açıldı";
            return;
        }

        // Eski sürümden kalan/henüz history'ye yazılmamış geçici çıktı için geriye dönük fallback.
        var image = Path.Combine(settings.RustServerPath, "mapimages", $"CustomGenerator{settings.WorldSize}_{settings.Seed}.png");
        var reports = Path.Combine(settings.RustServerPath, "HarmonyConfig", "RavenMapReports");
        var map = Path.Combine(settings.RustServerPath, "maps", $"CustomGenerator{settings.WorldSize}_{settings.Seed}.map");
        if (!File.Exists(image))
        {
            MessageBox.Show(this, "Seçili seed ve boyut için kalıcı üretim geçmişinde veya Rust çalışma klasöründe harita bulunamadı.", "Harita bulunamadı");
            return;
        }
        result = new(map, "", Path.GetDirectoryName(image)!, 0, 0, image, reports, settings.WorldSize, settings.Seed);
        LoadMap(image, reports, settings.WorldSize, settings.Seed);
        Navigate("Studio");
    }

    private void ExportPng_Click(object sender, RoutedEventArgs e) { var dialog = new SaveFileDialog { Filter = "PNG haritası|*.png", FileName = settings.MapName + ".png" }; if (dialog.ShowDialog(this) == true) { MapView.Export(dialog.FileName); TopStatus.Text = "PNG kaydedildi"; } }
    private void ExportMap_Click(object sender, RoutedEventArgs e) { if (result is null || !File.Exists(result.MapPath)) return; var dialog = new SaveFileDialog { Filter = "Rust haritası|*.map", FileName = settings.MapName + ".map" }; if (dialog.ShowDialog(this) == true) { File.Copy(result.MapPath, dialog.FileName, true); TopStatus.Text = "MAP kaydedildi"; } }
    private void BrowseRust_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "RustDedicated.exe bulunan veya kurulacak Rust sunucu klasörünü seçin",
            SelectedPath = RustPathBox.Text,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        RustPathBox.Text = Path.GetFullPath(dialog.SelectedPath);
        settings.RustServerPath = RustPathBox.Text;
        SettingsStore.Save(settings);
        UpdateRustEngineStatus(rustEngineService.Check(settings.RustServerPath));
    }

    private void InstallRustEngine_Click(object sender, RoutedEventArgs e)
    {
        SyncUiToSettings();
        if (rustUpdateAvailable)
            ShowRustEngineSetup(false, forceUpdate: true);
        else
            ShowRustEngineSetup(false, rustEngineService.Check(settings.RustServerPath).IsReady);
    }
    private void BrowseOutput_Click(object sender, RoutedEventArgs e) { using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "MAP ve PNG klasörlerinin kaydedileceği ana klasörü seçin", SelectedPath = OutputPathBox.Text }; if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) { OutputPathBox.Text = dialog.SelectedPath; settings.MapsOutputPath = dialog.SelectedPath; SettingsStore.Save(settings); } }
    private void ResetOutput_Click(object sender, RoutedEventArgs e) { settings.MapsOutputPath = ""; OutputPathBox.Text = SettingsStore.DefaultMapsRoot; SettingsStore.Save(settings); }
    private void OpenConfig_Click(object sender, RoutedEventArgs e) => OpenFolder(SettingsStore.ConfigRoot);
    private void OpenMaps_Click(object sender, RoutedEventArgs e) => OpenFolder(SettingsStore.OutputRoot(settings));
    private void OpenCurrentProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        var projectPath = settings.CurrentProjectPath;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            MessageBox.Show(this, "Bu harita henüz bir ayar dosyasına kaydedilmedi.", "Ayar dosyası");
            return;
        }

        var folder = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            MessageBox.Show(this, "Ayar dosyasının klasörü bulunamadı.", "Ayar dosyası");
            return;
        }

        OpenFolder(folder);
    }
    private static void OpenFolder(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
}
