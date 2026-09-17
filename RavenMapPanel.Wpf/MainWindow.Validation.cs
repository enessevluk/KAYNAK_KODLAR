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
    private void Validate_Click(object sender, RoutedEventArgs e) => ValidateMap();
    private void ValidateMap()
    {
        var flow = ValidationText.Document;
        flow.Blocks.Clear();
        flow.PagePadding = new Thickness(0);

        var normal = (System.Windows.Media.Brush)FindResource("TextBrush");
        var muted = (System.Windows.Media.Brush)FindResource("MutedBrush");
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6F, 0xDB, 0xAE));
        var error = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x68, 0x68));
        var warning = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xC8, 0x5B));

        void AddLine(string text, System.Windows.Media.Brush brush, FontWeight? weight = null)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                Padding = new Thickness(0),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text")
            };
            paragraph.Inlines.Add(new Run(text)
            {
                Foreground = brush,
                FontWeight = weight ?? FontWeights.Normal
            });
            flow.Blocks.Add(paragraph);
        }

        if (MapView.Document is null)
        {
            AddLine("Henüz doğrulanacak harita yüklenmedi.", muted);
            return;
        }

        var doc = MapView.Document;
        var results = MapValidationService.Evaluate(doc.Markers.Select(x => x.Category), catalog);
        var activeRules = results.Select(x => x.Rule).ToList();
        var failedCount = results.Count(x => !x.IsValid);
        ValidationTotalText.Text = results.Count.ToString();
        ValidationSuccessText.Text = (results.Count - failedCount).ToString();
        ValidationErrorText.Text = failedCount.ToString();
        UpdateProductionSummary(doc, appendToLogs: false);

        AddLine($"Harita: {settings.MapName}", normal, FontWeights.SemiBold);
        AddLine($"Boyut / Seed: {doc.WorldSize} / {settings.Seed}", normal);
        AddLine("Seçtiğiniz monument kuralları üretilen haritanın gerçek içeriğiyle karşılaştırıldı.", muted);
        AddLine("", normal);

        if (failedCount == 0)
            AddLine("✓ TÜM SEÇİLİ MONUMENT KURALLARI UYGULANDI", success, FontWeights.Bold);
        else
            AddLine($"✕ {failedCount} MONUMENT KURALI UYGULANMADI", error, FontWeights.Bold);

        AddLine("", normal);

        foreach (var item in results.OrderBy(x => x.IsValid).ThenBy(x => x.Rule.Name))
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                Padding = new Thickness(0),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text")
            };
            var statusBrush = item.IsValid ? success : error;
            paragraph.Inlines.Add(new Run(item.IsValid ? "✓" : "✕")
            {
                Foreground = statusBrush,
                FontWeight = FontWeights.Bold
            });
            var resultText = item.IsValid
                ? $"  {item.Rule.Name} — uygulandı"
                : $"  {DescribeValidationFailure(item)}";
            paragraph.Inlines.Add(new Run(resultText)
            {
                // Kullanıcının istediği görünürlük: false/eksik satırın tamamı kırmızı.
                Foreground = item.IsValid ? normal : error,
                FontWeight = item.IsValid ? FontWeights.Normal : FontWeights.SemiBold
            });
            flow.Blocks.Add(paragraph);
        }

        var terrain = doc.TerrainSafety;
        if (terrain is { Enabled: true })
        {
            AddLine("", normal);
            AddLine("ARAZİ GÜVENLİĞİ", normal, FontWeights.Bold);
            var mode = terrain.Mode.Equals("deep", StringComparison.OrdinalIgnoreCase) ? "Derin kontrol" : "Hızlı kontrol";
            AddLine($"✓ {mode}: {terrain.Checked}/{terrain.Candidates} taş örneği · {terrain.ElapsedMs} ms", success, FontWeights.SemiBold);
            if (terrain.BudgetExceeded)
                AddLine($"⚠ Süre bütçesi doldu ({terrain.BudgetMs} ms); kalan nesneler atlandı ve üretim bekletilmedi.", warning, FontWeights.SemiBold);
            if (!terrain.HeightMapAvailable)
                AddLine("⚠ Arazi yükseklik verisi okunamadı; hiçbir otomatik değişiklik yapılmadı.", warning, FontWeights.SemiBold);
            else if (!terrain.AlphaMapAvailable)
                AddLine("⚠ Alpha Map okunamadı; girilebilir arazi deliği kontrolü tamamlanamadı.", warning, FontWeights.SemiBold);
            else if (terrain.RepairCandidates == 0)
                AddLine("✓ İncelenen taşların altında Alpha Map deliği bulunmadı.", success);
            else
            {
                AddLine($"⚠ {terrain.RepairCandidates} taş Alpha Map deliğiyle çakışıyor. Oyun içinde girilebilir boşluk riski nedeniyle incelenmeli.", warning, FontWeights.SemiBold);
                foreach (var issue in terrain.Issues.Take(12))
                    AddLine($"  ⚠ {issue.Name} · X {issue.X:0}, Z {issue.Z:0} · Alpha deliği çakışması", warning);
                if (terrain.Issues.Count > 12)
                    AddLine($"  + {terrain.Issues.Count - 12} konum daha rapor klasöründe kayıtlı.", muted);
            }
            if (terrain.SlopeObservations > 0)
                AddLine($"Bilgi: {terrain.SlopeObservations} eğimli taş yerleşimi normal kabul edildi; tek başına hata sayılmadı.", muted);
            AddLine("Bilgi: Alpha Map kontrolü arazi deliğini denetler; taş modelinin fizik/collider boşluklarını garanti etmez.", muted);
            if (terrain.SafeAutoRepairEnabled)
                AddLine($"Güvenli onarım: {terrain.RepairsApplied} uygulandı · yalnız doğrulanmış profiller; God Rock, mağara ve bilinmeyen taşlar korunur.", muted);
        }

        if (activeRules.Count == 0)
            AddLine("Özel monument kuralı seçilmedi.", muted);
    }
}
