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
using Color = System.Windows.Media.Color;

namespace RavenMapPanel;

public partial class MainWindow : Window
{
    private string lastStudioLogText = "";
    private readonly Queue<string> studioRecentLogs = new();
    private readonly HashSet<string> collapsedSessionLogs = new(StringComparer.OrdinalIgnoreCase);

    private void AddMonument_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddMonumentWindow(catalog.Select(x => x.Category)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.CreatedRule is null) return;
        var rule = dialog.CreatedRule; catalog.Add(rule); var vm = new MonumentRuleViewModel(rule); vm.PropertyChanged += MonumentItem_PropertyChanged; MonumentItems.Add(vm); viewModel.IconItems.Add(vm); IconCache.Remove(rule.Icon);
        if (!CategoryBox.Items.Cast<object>().Any(x => string.Equals(Convert.ToString(x), rule.Category, StringComparison.CurrentCultureIgnoreCase))) CategoryBox.Items.Add(rule.Category);
        MonumentView.Refresh(); settings.Rules[rule.Id] = new RuleSetting { UseCustomPrefab = false, CustomPrefabVariantId = "" }; SettingsStore.Save(settings); TopStatus.Text = "Monument eklendi";
    }
    private void RandomSeed_Click(object sender, RoutedEventArgs e) { SeedBox.Text = Random.Shared.Next(1, int.MaxValue).ToString(); ScheduleSave(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => MapView.ZoomIn(); private void ZoomOut_Click(object sender, RoutedEventArgs e) => MapView.ZoomOut(); private void FitMap_Click(object sender, RoutedEventArgs e) => MapView.Fit();
    private void TogglePreviewSize_Click(object sender, RoutedEventArgs e) => SetPreviewExpanded(!previewExpanded);
    private void SetPreviewExpanded(bool expanded)
    {
        previewExpanded = expanded;
        StudioHeader.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        StudioHeaderRow.Height = expanded ? new GridLength(0) : new GridLength(76);
        StudioSettingsPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        StudioSettingsColumn.Width = expanded ? new GridLength(0) : new GridLength(380);
        StudioLogPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        StudioLogRow.Height = expanded ? new GridLength(0) : new GridLength(170);
        StudioMapArea.Margin = expanded ? new Thickness(0) : new Thickness(0, 0, 12, 0);
        ExpandPreviewButton.Content = expanded ? "▣" : "⛶";
        ExpandPreviewButton.ToolTip = expanded ? "Ayarları ve üretim durumunu göster" : "Büyük önizleme";
        Dispatcher.BeginInvoke(() => MapView.Fit(), DispatcherPriority.Loaded);
    }
    private void IconSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (IconSizeLabel is null || MapView is null) return; IconSizeLabel.Text = $"{e.NewValue:0} px"; MapView.IconSize = e.NewValue; MapView.InvalidateVisual(); if (IsLoaded) ScheduleSave(); }
    private void Layer_Changed(object sender, RoutedEventArgs e) { if (MapView is null || ShowIconsCheck is null || ShowEnvironmentCheck is null) return; MapView.ShowIcons = ShowIconsCheck.IsChecked == true; MapView.ShowEnvironment = ShowEnvironmentCheck.IsChecked == true; MapView.InvalidateVisual(); if (IsLoaded) ScheduleSave(); }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        StudioLog.Text = "";
        FullLog.Clear();
        lastStudioLogText = "";
        studioRecentLogs.Clear();
        collapsedSessionLogs.Clear();
        ResetProductionSummary();
    }

    private void ResetProductionSummary()
    {
        StudioLog.Text = "Bu oturumda henüz işlem yok.";
        if (LogSuccessSummary is null || LogFailureSummary is null || LogSummaryDetail is null) return;
        LogSuccessSummary.Text = "Başarılı: —";
        LogFailureSummary.Text = "Sorun: —";
        LogSummaryDetail.Text = "Henüz harita üretilmedi.";
    }

    private void AppendLog(string message)
    {
        var entry = FormatGenerationLog(message);
        if (entry is null) return;
        var (category, text, showInStudio) = entry.Value;
        AppendFriendlyLog(category, text, showInStudio);
    }

    private void AppendFriendlyLog(string category, string text, bool showInStudio = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var collapseRepeated = category is "HAZIRLIK" or "ÜRETİM" or "RAPOR" or "MONUMENT" or "HARİTA" or "KATMAN" or "GÖRSEL" or "DOĞRULAMA";
        if (collapseRepeated && !collapsedSessionLogs.Add(category + "|" + text)) return;

        var stamp = DateTime.Now.ToString("HH:mm:ss");
        FullLog.AppendText($"[{stamp}] [{category}]  {text}{Environment.NewLine}");
        FullLog.ScrollToEnd();

        if (!showInStudio || string.Equals(lastStudioLogText, text, StringComparison.Ordinal)) return;
        studioRecentLogs.Enqueue($"{stamp}  ·  {text}");
        while (studioRecentLogs.Count > 3) studioRecentLogs.Dequeue();
        StudioLog.Text = string.Join(Environment.NewLine, studioRecentLogs);
        lastStudioLogText = text;
        if (cancellation is not null || category is "HATA" or "UYARI" or "SONUÇ" or "EKSİK")
            SetStudioHeadline(text, category);
    }

    private static (string Category, string Text, bool ShowInStudio)? FormatGenerationLog(string raw)
    {
        var message = raw?.Trim();
        if (string.IsNullOrWhiteSpace(message)) return null;
        var lower = message.ToLowerInvariant();

        if (message.StartsWith("KUYRUK:", StringComparison.OrdinalIgnoreCase))
            return ("KUYRUK", message[7..].Trim(), true);
        if (message.StartsWith("HATA:", StringComparison.OrdinalIgnoreCase))
            return ("HATA", message[5..].Trim(), true);
        if (message.StartsWith("UYARI:", StringComparison.OrdinalIgnoreCase))
            return ("UYARI", message[6..].Trim(), true);

        if (lower.Contains("önceki sürüm otomatik yedeklendi")) return ("DOSYA", "Aynı isim/seed ile bulunan eski harita güvenli şekilde yedeklendi.", true);
        if (lower.Contains("üretim ayarları hazırlan")) return ("HAZIRLIK", "Harita ayarları ve üretim klasörleri hazırlanıyor…", true);
        if (lower.Contains("harmony bileşenleri ve kurallar hazırlan")) return ("HAZIRLIK", "Harita motoru ve Raven kuralları hazırlanıyor…", true);
        if (lower.Contains("özel monument prefab sistemi aktif")) return ("PREFAB", message, true);
        if (lower.Contains("seçili özel prefablar uygulanamadı")) return ("UYARI", "Bazı özel monument görünümleri uygulanamadı; ilgili monumentler vanilla görünümle üretilecek.", true);
        if (lower.Contains("kural politikası:")) return ("KURAL", "Monument kuralları harita üretimine aktarıldı; sonuçlar üretim sonunda doğrulanacak.", true);
        if (lower.Contains("harita üretiliyor") || lower.Contains("dünya ve monumentler oluşturuluyor")) return ("ÜRETİM", "Rust dünyası, biyomlar, yollar ve monumentler oluşturuluyor…", true);
        if (lower.Contains("yeni ikonlar doğru koordinatlara işleniyor")) return ("İKON", "Monument ve çevre ikonları doğru koordinatlara yerleştiriliyor…", true);
        if (lower.Contains("map ve png otomatik kaydedildi")) return ("DOSYA", "MAP, PNG, ham görsel ve rapor dosyaları çıktı klasörüne kaydedildi.", true);
        if (lower.StartsWith("god rock hedefi:")) return ("DOĞRULAMA", message, true);
        // Ham ikon sayısı kullanıcıya kural sonucunu anlatmıyor. Nihai sonuç aşağıda
        // gerçek monument doğrulamasından üretilen anlaşılır özetle gösterilir.
        if (lower.StartsWith("tamamlandı:")) return null;
        if (lower.Contains("teslim modu:")) return ("BİLGİ", "Harita teslim edildi; seçili monument kuralları ayrıca doğrulama sonucunda raporlanır.", false);
        if (lower.Contains("rust çalışma alanı temizlendi")) return ("TEMİZLİK", "Geçici Rust üretim dosyaları temizlendi.", false);
        if (lower.Contains("geçici üretim dosyası temizlenemedi")) return ("UYARI", message, false);

        // Raven reporter / CustomGenerator teknik satırlarını kullanıcı diline çevir.
        if (lower.Contains("done detected")) return ("ÜRETİM", "Rust dünya üretimini tamamladı; harita öğeleri raporlanıyor…", true);
        if (lower.Contains("png render finished")) return ("GÖRSEL", "Haritanın temel PNG görseli oluşturuldu.", true);
        if (lower.Contains("stage 1/4") && lower.Contains("starting")) return ("MONUMENT", "Haritadaki monumentler taranıyor ve konumları belirleniyor…", true);
        if (lower.Contains("stage 1/4") && lower.Contains("complete")) return ("MONUMENT", "Monument taraması tamamlandı.", true);
        if (lower.Contains("stage 2/4") && lower.Contains("starting")) return ("HARİTA", "Kaydedilen haritadaki prefab ve monument yerleşimleri kontrol ediliyor…", true);
        if (lower.Contains("stage 2/4") && lower.Contains("complete")) return ("HARİTA", "Prefab ve monument yerleşim taraması tamamlandı.", true);
        if (lower.Contains("stage 3/4") && lower.Contains("starting")) return ("KATMAN", "Elektrik hatları ve altyapı öğeleri taranıyor…", true);
        if (lower.Contains("stage 3/4") && lower.Contains("complete")) return ("KATMAN", "Elektrik hattı ve altyapı taraması tamamlandı.", true);
        if (lower.Contains("stage 4/4") && lower.Contains("starting")) return ("KATMAN", "Yol, nehir ve çevre topolojisi analiz ediliyor…", true);
        if (lower.Contains("report written") || lower.Contains("report file:")) return ("RAPOR", "Harita doğrulama raporu hazırlandı.", true);
        if (lower.Contains("godrock exact count") || lower.Contains("god rock exact count")) return ("DOĞRULAMA", "God Rock kesin adet kuralı uygulanıyor ve kontrol ediliyor…", true);
        if (lower.Contains("guaranteed placement") && (lower.Contains("validation") || lower.Contains("completed"))) return ("DOĞRULAMA", "Monument kuralları son kez kontrol ediliyor…", true);
        if (lower.Contains("[harmony] loaded") && lower.Contains("customgenerator")) return ("HAZIRLIK", "CustomGenerator harita üretim modülü yüklendi.", true);
        if (lower.Contains("generating") || lower.Contains("customgenerator") || lower.Contains("[cgen gen]")) return ("ÜRETİM", "Harita motoru arka planda dünya verilerini işliyor.", false);

        if (lower.Contains("fatal") || lower.Contains("exception") || lower.Contains(" error") || lower.Contains("failed"))
            return ("HATA", CleanTechnicalMessage(message), true);
        if (lower.Contains("warning") || lower.Contains("warn"))
            return ("UYARI", CleanTechnicalMessage(message), true);

        // Raven'ın kendi anlaşılır Türkçe mesajlarını koru, rastgele engine spamini gizle.
        if (!message.StartsWith("[", StringComparison.Ordinal) && message.Any(ch => ch is 'ğ' or 'Ğ' or 'ş' or 'Ş' or 'ı' or 'İ' or 'ç' or 'Ç' or 'ö' or 'Ö' or 'ü' or 'Ü'))
            return ("BİLGİ", message, true);

        return null;
    }

    private static string CleanTechnicalMessage(string value)
    {
        var text = value.Replace("[RavenWorldDataReporterV43]", "", StringComparison.OrdinalIgnoreCase)
            .Replace("[Raven Guaranteed Placement]", "", StringComparison.OrdinalIgnoreCase)
            .Replace("[CGen]", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return text.Length <= 320 ? text : text[..320] + "…";
    }

    private void SetStudioHeadline(string text, string category)
    {
        // Studio'da ayrı bir durum etiketi yoktur; tek, sade işlem paneli kullanılır.
    }

    private IReadOnlyList<MapRuleValidation> CurrentValidationResults()
    {
        if (MapView.Document is null) return Array.Empty<MapRuleValidation>();
        return MapValidationService.Evaluate(MapView.Document.Markers.Select(x => x.Category), catalog);
    }

    private void UpdateProductionSummary(MapDocument doc, bool appendToLogs, int mapNumber = 1, int mapCount = 1)
    {
        var validation = MapValidationService.Evaluate(doc.Markers.Select(x => x.Category), catalog);
        var failed = validation.Where(x => !x.IsValid).ToList();
        var successful = validation.Where(x => x.IsValid).ToList();

        LogSuccessSummary.Text = $"Başarılı: {successful.Count}";
        LogFailureSummary.Text = $"Sorun: {failed.Count}";

        if (validation.Count == 0)
        {
            LogSummaryDetail.Text = "Zorunlu veya yasaklı monument kuralı seçilmedi. Harita yine başarıyla üretildi.";
            SetStudioHeadline("✓ Harita başarıyla üretildi. Doğrulanacak özel monument kuralı yok.", "SONUÇ");
            if (appendToLogs) AppendFriendlyLog("SONUÇ", "Harita üretildi; doğrulanacak zorunlu/yasaklı monument kuralı yok.", true);
            return;
        }

        LogSummaryDetail.Text = failed.Count == 0
            ? $"{successful.Count} seçili kuralın tamamı karşılandı. Ayrıntılı sonuçları Doğrulama ekranında görebilirsin."
            : $"{successful.Count} kural başarılı · {failed.Count} kural kontrol edilmeli: " +
              string.Join(", ", failed.Take(4).Select(x => x.Rule.Name)) +
              (failed.Count > 4 ? $" ve {failed.Count - 4} kural daha" : "");

        if (!appendToLogs) return;
        var prefix = mapCount > 1 ? $"Harita {mapNumber}/{mapCount}: " : "";
        if (failed.Count == 0)
        {
            AppendFriendlyLog("SONUÇ", prefix + "Tüm seçili monument kuralları sorunsuz karşılandı.", true);
            SetStudioHeadline("✓ Tüm seçili monument kuralları sorunsuz karşılandı.", "SONUÇ");
        }
        else
        {
            var shortNames = string.Join(", ", failed.Take(3).Select(x => x.Rule.Name));
            if (failed.Count > 3) shortNames += $" ve {failed.Count - 3} kural daha";
            AppendFriendlyLog("SONUÇ", prefix + $"Harita oluşturuldu; {failed.Count} monument kuralı karşılanmadı.", true);
            foreach (var item in failed)
                AppendFriendlyLog("EKSİK", DescribeValidationFailure(item), true);
            SetStudioHeadline($"⚠ {failed.Count} kural karşılanmadı: {shortNames}", "EKSİK");
        }
    }

    private static string DescribeValidationFailure(MapRuleValidation item)
    {
        if (item.Rule.State == RuleState.Blocked)
            return $"{item.Rule.Name}: olmaması gerekiyordu, {item.Count} adet bulundu";

        var minimum = Math.Max(1, item.Rule.Minimum);
        var maximum = Math.Max(minimum, item.Rule.Maximum);
        if (item.Count < minimum)
            return $"{item.Rule.Name}: {item.Count} bulundu, en az {minimum} gerekli";
        if (item.Count > maximum)
            return $"{item.Rule.Name}: {item.Count} bulundu, en fazla {maximum} olmalı";
        return $"{item.Rule.Name}: beklenen kural karşılanmadı ({item.Count}, {item.Mode})";
    }

    private string CurrentValidationStatusDetail()
    {
        var validation = CurrentValidationResults();
        if (validation.Count == 0) return "Harita kaydedildi; doğrulanacak özel monument kuralı yok.";
        var failed = validation.Where(x => !x.IsValid).ToList();
        if (failed.Count == 0) return "Tüm seçili monument kuralları sorunsuz karşılandı.";
        var names = string.Join(", ", failed.Take(4).Select(x => x.Rule.Name));
        if (failed.Count > 4) names += $" ve {failed.Count - 4} kural daha";
        return $"{failed.Count} kural kontrol edilmeli: {names}";
    }

    private void ShowStudioValidationSummary()
    {
        var validation = CurrentValidationResults();
        var failed = validation.Where(x => !x.IsValid).ToList();
        if (failed.Count == 0)
        {
            StudioLog.Text = validation.Count == 0
                ? "✓ Harita hazır · kontrol edilecek zorunlu monument kuralı yok."
                : "✓ Tüm seçili monument kuralları uygulandı.";
            StudioLog.Foreground = new SolidColorBrush(Color.FromRgb(0x86, 0xE2, 0xB8));
            return;
        }

        var lines = new List<string> { $"✕ {failed.Count} monument kuralı uygulanmadı:" };
        lines.AddRange(failed.Select(x => "✕ " + DescribeValidationFailure(x)));
        lines.Add("Ayrıntılar için Doğrulama ekranını açın.");
        StudioLog.Text = string.Join(Environment.NewLine, lines);
        StudioLog.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x79, 0x79));
    }
}
