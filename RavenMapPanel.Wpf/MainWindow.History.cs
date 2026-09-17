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
    private void RecordGenerationHistory(GenerationResult generated, string? mapName = null, bool isImported = false)
    {
        if (MapView.Document is null) return;

        GenerationHistoryStore.Add(new GenerationHistoryEntry
        {
            MapName = string.IsNullOrWhiteSpace(mapName) ? settings.MapName : mapName,
            Seed = generated.Seed,
            WorldSize = generated.WorldSize,
            GeneratedAt = DateTime.Now,
            IconCount = MapView.Document.Markers.Count,
            GodRockCount = MapView.Document.Markers.Count(x => x.Category == "god_rocks"),
            ValidationIssues = CountValidationIssues(MapView.Document),
            MapPath = generated.MapPath,
            ImagePath = generated.ImagePath,
            RawImagePath = generated.RawImagePath,
            ReportFolder = generated.ReportFolder,
            OutputFolder = generated.OutputFolder,
            ProjectPath = settings.CurrentProjectPath,
            BackupPath = generated.BackupPath,
            IsImported = isImported
        });
        RefreshHistory();
    }

    private int CountValidationIssues(MapDocument doc) =>
        MapValidationService.CountIssues(doc.Markers.Select(x => x.Category), catalog);

    private void RefreshHistory_Click(object sender, RoutedEventArgs e) => RefreshHistory();

    private async void ImportMap_Click(object sender, RoutedEventArgs e)
    {
        if (maintenanceActive)
        {
            MessageBox.Show(this, "Raven bakım modundayken MAP analizi başlatılamaz.", "Raven bakımda", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var fileDialog = new OpenFileDialog
        {
            Title = "İçe aktarılacak Rust MAP dosyasını seçin",
            Filter = "Rust haritası (*.map)|*.map",
            CheckFileExists = true,
            Multiselect = false
        };
        if (fileDialog.ShowDialog(this) != true) return;

        SyncUiToSettings();
        RustMapMetadata metadata;
        try
        {
            metadata = RustMapMetadataReader.Read(fileDialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "MAP dosyasının boyutu otomatik algılanamadı.\n\n" + ex.Message,
                "MAP içe aktar", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var mapDisplayName = SettingsStore.SafeName(Path.GetFileNameWithoutExtension(fileDialog.FileName), "IçeAktarilanHarita");

        var licenseCheck = await App.LicenseService.RequireOnlineEntitlementAsync();
        if (!licenseCheck.IsValid)
        {
            if (licenseCheck.State != LicenseCheckState.ServerUnavailable)
                await RevokeOpenSessionAsync(licenseCheck);
            else
                MessageBox.Show(this, licenseCheck.Message, "Raven lisans doğrulaması", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!EnsureRustEngineReady(true)) return;

        var importSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))
            ?? throw new InvalidOperationException("MAP analiz ayarları hazırlanamadı.");
        importSettings.MapName = mapDisplayName;
        importSettings.WorldSize = metadata.WorldSize;
        importSettings.Seed = metadata.Seed;
        importSettings.GenerationCount = 1;

        cancellation = new CancellationTokenSource();
        GenerateButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ExportPngButton.IsEnabled = ExportMapButton.IsEnabled = false;
        GenerationProgress.IsIndeterminate = true;
        SideProgressTitle.Text = "MAP analiz ediliyor";
        SideProgressDetail.Text = $"{Path.GetFileName(fileDialog.FileName)} · {metadata.WorldSize} metre · otomatik algılandı";
        TopStatus.Text = "MAP içe aktarılıyor";
        StudioLog.Text = "";
        FullLog.Clear();
        lastStudioLogText = "";
        studioRecentLogs.Clear();
        collapsedSessionLogs.Clear();
        ResetProductionSummary();
        AppendFriendlyLog("İÇE AKTAR", $"{Path.GetFileName(fileDialog.FileName)} analiz kuyruğuna alındı.", true);
        Navigate("Studio");

        try
        {
            var service = new GenerationService(catalog, App.LicenseService);
            service.Log += message => Dispatcher.Invoke(() => AppendLog(message));
            result = await service.AnalyzeImportedMapAsync(fileDialog.FileName, importSettings, cancellation.Token);
            LoadMap(result.RawImagePath, result.ReportFolder, result.WorldSize, result.Seed);
            UpdateProductionSummary(MapView.Document!, appendToLogs: true);
            RecordGenerationHistory(result, mapDisplayName, isImported: true);
            ApplyMapContext(mapDisplayName, result.WorldSize, result.Seed, imported: true);
            GenerationProgress.IsIndeterminate = false;
            GenerationProgress.Value = 100;
            SideProgressTitle.Text = "MAP içe aktarıldı";
            SideProgressDetail.Text = "Harita ve monument doğrulama raporu hazır.";
            TopStatus.Text = "İçe aktarılan harita hazır";
            ExportPngButton.IsEnabled = ExportMapButton.IsEnabled = true;
            AppendFriendlyLog("SONUÇ", "MAP analizi tamamlandı; harita kütüphaneye eklendi.", true);
        }
        catch (OperationCanceledException)
        {
            SideProgressTitle.Text = "MAP analizi iptal edildi";
            SideProgressDetail.Text = "Kaynak MAP dosyasında değişiklik yapılmadı.";
            TopStatus.Text = "İptal edildi";
        }
        catch (Exception ex)
        {
            AppendFriendlyLog("HATA", ex.Message, true);
            SideProgressTitle.Text = "MAP analiz edilemedi";
            SideProgressDetail.Text = ex.Message;
            TopStatus.Text = "İçe aktarma hatası";
            MessageBox.Show(this, "MAP dosyası analiz edilemedi.\n\n" + ex.Message,
                "MAP içe aktar", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void RefreshHistory()
    {
        if (HistoryList is null) return;

        var previousA = (CompareABox.SelectedItem as GenerationHistoryEntry)?.Id;
        var previousB = (CompareBBox.SelectedItem as GenerationHistoryEntry)?.Id;
        var items = GenerationHistoryStore.LoadExisting()
            .OrderByDescending(x => x.GeneratedAt)
            .ToList();

        GenerationHistoryItems.Clear();
        foreach (var item in items) GenerationHistoryItems.Add(item);
        HistoryCountText.Text = $"{GenerationHistoryItems.Count} kayıt";

        CompareABox.SelectedItem = GenerationHistoryItems.FirstOrDefault(x => x.Id == previousA);
        CompareBBox.SelectedItem = GenerationHistoryItems.FirstOrDefault(x => x.Id == previousB);

        if (CompareABox.SelectedItem is null && GenerationHistoryItems.Count > 1)
            CompareABox.SelectedIndex = 1;
        else if (CompareABox.SelectedItem is null && GenerationHistoryItems.Count == 1)
            CompareABox.SelectedIndex = 0;

        if (CompareBBox.SelectedItem is null && GenerationHistoryItems.Count > 0)
            CompareBBox.SelectedIndex = 0;

        if (GenerationHistoryItems.Count > 1 &&
            CompareABox.SelectedItem is GenerationHistoryEntry selectedA &&
            CompareBBox.SelectedItem is GenerationHistoryEntry selectedB &&
            selectedA.Id == selectedB.Id)
        {
            CompareABox.SelectedIndex = 1;
            CompareBBox.SelectedIndex = 0;
        }

        if (GenerationHistoryItems.Count == 0)
        {
            CompareSummaryText.Text = "Henüz üretim geçmişi yok.";
            ComparisonItems.Clear();
        }

        ResetComparisonSummary();
    }

    private void ResetComparisonSummary()
    {
        if (CompareIconCountA is null) return;
        CompareIconCountA.Text = "";
        CompareIconCountB.Text = "";
        CompareChangedCount.Text = "—";
        var selectedA = CompareABox.SelectedItem as GenerationHistoryEntry;
        var selectedB = CompareBBox.SelectedItem as GenerationHistoryEntry;
        CompareMapNameA.Text = selectedA?.DisplayName ?? "Harita seçilmedi";
        CompareMapNameB.Text = selectedB?.DisplayName ?? "Harita seçilmedi";
        CompareTableHeaderA.Text = selectedA?.DisplayName ?? "Harita A";
        CompareTableHeaderB.Text = selectedB?.DisplayName ?? "Harita B";
        CompareTableHeaderA.ToolTip = selectedA?.DisplayName;
        CompareTableHeaderB.ToolTip = selectedB?.DisplayName;
        ComparisonEmptyTitle.Text = "Karşılaştırma bekleniyor";
        ComparisonEmptyDetail.Text = "İki farklı harita seçildiğinde yalnız değişen monument ve katmanlar burada gösterilir.";
    }

    private void LoadHistory_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not GenerationHistoryEntry entry) return;
        if (!File.Exists(entry.RawImagePath) ||
            !File.Exists(Path.Combine(entry.ReportFolder, $"RavenMapReport_{entry.WorldSize}_{entry.Seed}.json")))
        {
            MessageBox.Show(this,
                "Bu geçmiş kaydının önizleme dosyaları bulunamadı.",
                "Harita geçmişi",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        result = new GenerationResult(entry.MapPath, entry.ImagePath, entry.OutputFolder,
            entry.IconCount, entry.GodRockCount, entry.RawImagePath, entry.ReportFolder,
            entry.WorldSize, entry.Seed, entry.BackupPath);
        LoadMap(entry.RawImagePath, entry.ReportFolder, entry.WorldSize, entry.Seed);
        ApplyMapContext(entry.MapName, entry.WorldSize, entry.Seed, entry.IsImported);
        Navigate("Studio");
        TopStatus.Text = "Geçmiş harita açıldı";
    }

    private void ApplyMapContext(string mapName, int worldSize, int seed, bool imported)
    {
        suppressProjectChangeTracking = true;
        try
        {
            settings.MapName = mapName;
            settings.WorldSize = worldSize;
            settings.Seed = seed;
            MapNameBox.Text = mapName;
            SizeBox.Text = worldSize.ToString();
            SeedBox.Text = seed.ToString();
            SettingsActiveMapName.Text = mapName;
            ProjectTitle.Text = mapName;
            ProjectMetaText.Text = $"  · {worldSize} · {(imported ? "İçe aktarıldı" : "Procedural")}";
            SettingsStore.Save(settings);
        }
        finally
        {
            suppressProjectChangeTracking = false;
        }
    }

    private void OpenHistoryFolder_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not GenerationHistoryEntry entry) return;
        if (!Directory.Exists(entry.OutputFolder))
        {
            MessageBox.Show(this, "Harita klasörü artık mevcut değil.", "Harita geçmişi");
            return;
        }
        OpenFolder(entry.OutputFolder);
    }

    private void OpenHistoryBackup_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not GenerationHistoryEntry entry ||
            string.IsNullOrWhiteSpace(entry.BackupPath) ||
            !Directory.Exists(entry.BackupPath))
            return;
        OpenFolder(entry.BackupPath);
    }

    private void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not GenerationHistoryEntry entry)
            return;

        if (!TryGetSafeHistoryOutputFolder(entry, out var outputFolder, out var safetyError))
        {
            MessageBox.Show(this,
                safetyError,
                "Haritayı sil",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var answer = MessageBox.Show(this,
            $"Bu haritanın klasörü kalıcı olarak silinecek:\n\n{outputFolder}\n\n" +
            $"{entry.DisplayName}\n\nBu işlem geri alınamaz. Devam edilsin mi?",
            "Haritayı sil",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            // Kullanıcı klasörü daha önce elle silmişse yine geçmiş kaydını temizle.
            if (Directory.Exists(outputFolder))
                Directory.Delete(outputFolder, true);

            GenerationHistoryStore.Remove(entry.Id);

            if (result is not null &&
                PathsEqual(result.OutputFolder, outputFolder))
            {
                result = null;
                ExportPngButton.IsEnabled = false;
                ExportMapButton.IsEnabled = false;
            }

            RefreshHistory();
            TopStatus.Text = "Harita silindi";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Harita klasörü silinemedi.\n\n" + ex.Message,
                "Haritayı sil",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static bool TryGetSafeHistoryOutputFolder(
        GenerationHistoryEntry entry,
        out string outputFolder,
        out string error)
    {
        outputFolder = "";
        error = "";

        try
        {
            if (string.IsNullOrWhiteSpace(entry.OutputFolder))
            {
                error = "Bu geçmiş kaydında harita klasörü bilgisi yok.";
                return false;
            }

            outputFolder = Path.GetFullPath(entry.OutputFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var root = Path.GetPathRoot(outputFolder)?
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.IsNullOrWhiteSpace(root) ||
                PathsEqual(outputFolder, root))
            {
                error = "Güvenlik nedeniyle disk kök klasörü silinemez.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(entry.MapPath))
            {
                error = "Harita dosyasının yolu doğrulanamadı; klasör silme işlemi iptal edildi.";
                return false;
            }

            var mapPath = Path.GetFullPath(entry.MapPath);
            var mapDirectory = Path.GetDirectoryName(mapPath);

            // GenerationService .map dosyasını doğrudan OutputFolder içine teslim eder.
            // History JSON bozulursa yanlış bir klasörü recursive silmemek için bunu şart koşuyoruz.
            if (string.IsNullOrWhiteSpace(mapDirectory) ||
                !PathsEqual(mapDirectory, outputFolder))
            {
                error = "Harita klasörü ile .map dosyası yolu eşleşmiyor. Güvenlik nedeniyle silme işlemi iptal edildi.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = "Harita klasörü doğrulanamadı: " + ex.Message;
            return false;
        }
    }

    private static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        try
        {
            var left = Path.GetFullPath(a)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var right = Path.GetFullPath(b)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void CompareHistory_Click(object sender, RoutedEventArgs e)
    {
        if (CompareABox.SelectedItem is not GenerationHistoryEntry a ||
            CompareBBox.SelectedItem is not GenerationHistoryEntry b)
        {
            CompareSummaryText.Text = "Karşılaştırmak için iki üretim seçin.";
            ComparisonItems.Clear();
            ResetComparisonSummary();
            return;
        }

        if (a.Id == b.Id)
        {
            CompareSummaryText.Text = "Karşılaştırma için iki farklı üretim seçin.";
            ComparisonItems.Clear();
            ResetComparisonSummary();
            return;
        }

        try
        {
            var docA = MapDataService.Load(a.RawImagePath, a.ReportFolder, a.WorldSize, a.Seed, catalog);
            var docB = MapDataService.Load(b.RawImagePath, b.ReportFolder, b.WorldSize, b.Seed, catalog);
            var countsA = docA.Markers.GroupBy(x => x.Category)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            var countsB = docB.Markers.GroupBy(x => x.Category)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

            var ids = countsA.Keys.Concat(countsB.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var rows = ids.Select(id =>
            {
                var rule = catalog.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                return new MapComparisonRow
                {
                    Name = rule?.Name ?? id,
                    CountA = countsA.GetValueOrDefault(id),
                    CountB = countsB.GetValueOrDefault(id)
                };
            })
            .OrderByDescending(x => Math.Abs(x.Difference))
            .ThenBy(x => x.Name)
            .ToList();

            var changed = rows.Count(x => x.Difference != 0);
            ComparisonItems.Clear();
            foreach (var row in rows.Where(x => x.Difference != 0)) ComparisonItems.Add(row);

            CompareIconCountA.Text = "";
            CompareIconCountB.Text = "";
            CompareChangedCount.Text = changed.ToString();
            CompareMapNameA.Text = a.DisplayName;
            CompareMapNameB.Text = b.DisplayName;
            CompareTableHeaderA.Text = a.DisplayName;
            CompareTableHeaderB.Text = b.DisplayName;
            CompareTableHeaderA.ToolTip = a.DisplayName;
            CompareTableHeaderB.ToolTip = b.DisplayName;
            CompareSummaryText.Text =
                $"A = {a.DisplayName}  ·  B = {b.DisplayName}  ·  {changed} öğede fark";
            ComparisonEmptyTitle.Text = changed == 0 ? "Dağılımlar aynı" : "Değişen öğe bulunamadı";
            ComparisonEmptyDetail.Text = changed == 0
                ? "Seçilen iki haritanın monument ve katman adetleri aynıdır."
                : "Karşılaştırma sonucu değişen öğeler burada listelenir.";
        }
        catch (Exception ex)
        {
            ComparisonItems.Clear();
            CompareSummaryText.Text = "Karşılaştırma yapılamadı.";
            ResetComparisonSummary();
            MessageBox.Show(this, ex.Message, "Harita karşılaştırma",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
