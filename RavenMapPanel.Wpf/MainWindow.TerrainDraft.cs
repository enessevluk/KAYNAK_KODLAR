using System.Windows;
using System.Windows.Controls;

namespace RavenMapPanel;

public partial class MainWindow
{
    private bool terrainDraftVisible;

    private void InitializeTerrainDraftEditor()
    {
        settings.TerrainDraft ??= new TerrainDraft();
        TerrainDraftView.DraftChanged += TerrainDraftView_DraftChanged;
        TerrainDraftView.SetDraft(settings.TerrainDraft);
    }

    private void LoadTerrainDraftIntoUi()
    {
        settings.TerrainDraft ??= new TerrainDraft();
        TerrainDraftView.SetDraft(settings.TerrainDraft);
        TerrainDraftEnabledCheck.IsChecked = settings.TerrainDraft.Enabled;
        StrictMonumentRulesCheck.IsChecked = settings.StrictMonumentRules;
        TerrainBrushSizeSlider.Value = Math.Clamp(settings.TerrainDraft.BrushRadius, 0.02, 0.22);
        foreach (var item in TerrainBrushBox.Items.OfType<ComboBoxItem>())
        {
            if (!Enum.TryParse<TerrainBrushKind>(Convert.ToString(item.Tag), out var kind) || kind != settings.TerrainDraft.ActiveBrush)
                continue;
            TerrainBrushBox.SelectedItem = item;
            break;
        }
        if (TerrainBrushBox.SelectedIndex < 0) TerrainBrushBox.SelectedIndex = 1;
        UpdateTerrainDraftSummary();
    }

    private void TerrainDraftView_DraftChanged(object? sender, EventArgs e)
    {
        UpdateTerrainDraftSummary();
        ScheduleSave();
    }

    private void TerrainDraftSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || suppressProjectChangeTracking) return;
        settings.TerrainDraft.Enabled = TerrainDraftEnabledCheck.IsChecked == true;
        settings.StrictMonumentRules = StrictMonumentRulesCheck.IsChecked == true;
        UpdateTerrainDraftSummary();
        ScheduleSave();
    }

    private void TerrainBrush_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TerrainBrushBox.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<TerrainBrushKind>(Convert.ToString(item.Tag), out var kind)) return;
        settings.TerrainDraft.ActiveBrush = kind;
        TerrainDraftView.ActiveBrush = kind;
        if (IsLoaded && !suppressProjectChangeTracking) ScheduleSave();
    }

    private void TerrainBrushSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TerrainBrushSizeText is null) return;
        settings.TerrainDraft.BrushRadius = e.NewValue;
        TerrainDraftView.BrushRadius = e.NewValue;
        TerrainBrushSizeText.Text = $"%{e.NewValue * 100:0}";
        if (IsLoaded && !suppressProjectChangeTracking) ScheduleSave();
    }

    private void TerrainUndo_Click(object sender, RoutedEventArgs e)
    {
        if (!TerrainDraftView.Undo()) TopStatus.Text = "Geri alınacak arazi fırçası yok";
    }

    private void TerrainRedo_Click(object sender, RoutedEventArgs e)
    {
        if (!TerrainDraftView.Redo()) TopStatus.Text = "Yinelenecek arazi fırçası yok";
    }

    private void TerrainClear_Click(object sender, RoutedEventArgs e)
    {
        if (TerrainDraftView.SampleCount == 0) return;
        if (MessageBox.Show(this, "Arazi taslağındaki bütün fırça çizimleri silinsin mi?", "Arazi taslağını temizle",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        TerrainDraftView.ClearDraft();
        TopStatus.Text = "Arazi taslağı temizlendi";
    }

    private void ToggleTerrainDraft_Click(object sender, RoutedEventArgs e)
    {
        terrainDraftVisible = !terrainDraftVisible;
        TerrainDraftView.Visibility = terrainDraftVisible ? Visibility.Visible : Visibility.Collapsed;
        MapView.Visibility = terrainDraftVisible ? Visibility.Collapsed : Visibility.Visible;
        MarkerDetailsCard.Visibility = Visibility.Collapsed;
        TerrainDraftToggleButton.Content = terrainDraftVisible ? "🗺 Harita" : "✎ Taslak";
        MapCanvasStatus.Text = terrainDraftVisible ? "Arazi taslağı · Önizleme" : "Harita önizlemesi";
    }

    private void UpdateTerrainDraftSummary()
    {
        if (TerrainDraftCountText is null) return;
        var state = settings.TerrainDraft.Enabled ? "Etkin" : "Kapalı";
        TerrainDraftCountText.Text = $"{state} · {TerrainDraftView.SampleCount:N0} fırça noktası";
    }
}
