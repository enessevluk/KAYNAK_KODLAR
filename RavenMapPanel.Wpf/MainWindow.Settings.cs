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
    private void LoadSettingsIntoUi()
    {
        suppressProjectChangeTracking = true;
        try
        {
        MapNameBox.Text = settings.MapName; SeedBox.Text = settings.Seed.ToString(); SizeBox.Text = settings.WorldSize.ToString(); RustPathBox.Text = settings.RustServerPath;
        GenerationCountBox.SelectedIndex = Math.Clamp(settings.GenerationCount, 1, 7) - 1;
        RoadsCheck.IsChecked = settings.Roads; RiversCheck.IsChecked = settings.Rivers; PowerCheck.IsChecked = settings.PowerLines; RailsCheck.IsChecked = settings.Rails; MetroCheck.IsChecked = settings.UndergroundRails;
        OasisCheck.IsChecked = settings.Oasis; CanyonCheck.IsChecked = settings.Canyons; LakesCheck.IsChecked = settings.Lakes;
        Tier0Slider.Value = settings.Tier0; Tier1Slider.Value = settings.Tier1; Tier2Slider.Value = settings.Tier2;
        BiomeAridSlider.Value = settings.BiomeArid; BiomeTemperateSlider.Value = settings.BiomeTemperate; BiomeTundraSlider.Value = settings.BiomeTundra; BiomeArcticSlider.Value = settings.BiomeArctic; BiomeJungleSlider.Value = settings.BiomeJungle;
        TerrainFastCheck.IsChecked = settings.TerrainFastCheck; TerrainDeepCheck.IsChecked = settings.TerrainDeepCheck; TerrainSafeRepairCheck.IsChecked = settings.TerrainSafeAutoRepair;
        LoadTerrainDraftIntoUi();
        UpdateAdvancedLabels();
        foreach (var vm in MonumentItems)
        {
            if (settings.Rules.TryGetValue(vm.Id, out var saved))
            {
                vm.Rule.Minimum = saved.Minimum; vm.Rule.Maximum = saved.Maximum; vm.State = saved.State;
                var selectedVariant = !string.IsNullOrWhiteSpace(saved.CustomPrefabVariantId)
                    ? saved.CustomPrefabVariantId
                    : saved.UseCustomPrefab ? "custom" : "";
                vm.SetPrefabVariants(viewModel.CustomPrefabService.GetVariants(vm.Id), selectedVariant);
                vm.NotifyRuleValuesChanged();
            }
            else
            {
                vm.State = RuleState.Optional; vm.SelectedPrefabVariantId = ""; vm.NotifyRuleValuesChanged();
            }
        }
        IconSizeSlider.Value = settings.IconSize; ShowIconsCheck.IsChecked = settings.ShowRavenIcons; ShowEnvironmentCheck.IsChecked = settings.ShowEnvironmentIcons;
        MapView.IconSize = settings.IconSize; MapView.ShowIcons = settings.ShowRavenIcons; MapView.ShowEnvironment = settings.ShowEnvironmentIcons;
        OutputPathBox.Text = SettingsStore.OutputRoot(settings);
        DefaultMapsText.Text = "Varsayılan konum: " + SettingsStore.DefaultMapsRoot;
        CurrentProjectText.Text = string.IsNullOrWhiteSpace(settings.CurrentProjectPath) ? "Henüz kaydedilmedi." : settings.CurrentProjectPath;
        SettingsActiveMapName.Text = string.IsNullOrWhiteSpace(settings.MapName) ? "RavenHarita" : settings.MapName;
        ProjectTitle.Text = string.IsNullOrWhiteSpace(settings.MapName) ? "Yeni harita" : settings.MapName;
        ProjectMetaText.Text = $"  · {settings.WorldSize} · Procedural";
        UpdateRustEngineStatus(rustEngineService.Check(settings.RustServerPath));
        UpdateSummaries();
        }
        finally
        {
            suppressProjectChangeTracking = false;
        }
    }

    private void SyncUiToSettings()
    {
        settings.MapName = string.IsNullOrWhiteSpace(MapNameBox.Text) ? "RavenHarita" : MapNameBox.Text.Trim();
        if (int.TryParse(SeedBox.Text, out var seed) && seed > 0) settings.Seed = seed;
        if (int.TryParse(SizeBox.Text, out var size) && size is >= 1000 and <= 6000) settings.WorldSize = size;
        settings.GenerationCount = Math.Clamp(GenerationCountBox.SelectedIndex + 1, 1, 7);
        settings.RustServerPath = RustPathBox.Text.Trim(); settings.Roads = RoadsCheck.IsChecked == true; settings.Rivers = RiversCheck.IsChecked == true; settings.PowerLines = PowerCheck.IsChecked == true; settings.Rails = RailsCheck.IsChecked == true; settings.UndergroundRails = MetroCheck.IsChecked == true;
        settings.MapsOutputPath = OutputPathBox.Text.Equals(SettingsStore.DefaultMapsRoot, StringComparison.OrdinalIgnoreCase) ? "" : OutputPathBox.Text.Trim();
        settings.Oasis = OasisCheck.IsChecked == true; settings.Canyons = CanyonCheck.IsChecked == true; settings.Lakes = LakesCheck.IsChecked == true;
        settings.Tier0 = Tier0Slider.Value; settings.Tier1 = Tier1Slider.Value; settings.Tier2 = Tier2Slider.Value;
        settings.BiomeArid = BiomeAridSlider.Value; settings.BiomeTemperate = BiomeTemperateSlider.Value; settings.BiomeTundra = BiomeTundraSlider.Value; settings.BiomeArctic = BiomeArcticSlider.Value; settings.BiomeJungle = BiomeJungleSlider.Value;
        settings.TerrainFastCheck = TerrainFastCheck.IsChecked == true; settings.TerrainDeepCheck = TerrainDeepCheck.IsChecked == true; settings.TerrainSafeAutoRepair = TerrainSafeRepairCheck.IsChecked == true;
        settings.TerrainDraft.Enabled = TerrainDraftEnabledCheck.IsChecked == true;
        settings.TerrainDraft.BrushRadius = TerrainBrushSizeSlider.Value;
        settings.StrictMonumentRules = StrictMonumentRulesCheck.IsChecked == true;
        settings.IconSize = IconSizeSlider.Value; settings.ShowRavenIcons = ShowIconsCheck.IsChecked == true; settings.ShowEnvironmentIcons = ShowEnvironmentCheck.IsChecked == true;
        settings.Rules = catalog.Where(x => !x.IsWorldFeature).ToDictionary(x => x.Id, x => new RuleSetting
        {
            State = x.State,
            Minimum = x.Minimum,
            Maximum = x.Maximum,
            UseCustomPrefab = x.UseCustomPrefab,
            CustomPrefabVariantId = x.CustomPrefabVariantId
        });
        UpdateSummaries();
    }

    private void UpdateSummaries()
    {
        var mapName = string.IsNullOrWhiteSpace(MapNameBox.Text) ? "Raven Harita" : MapNameBox.Text.Trim();
        StudioRequiredCount.Text=$"{MonumentItems.Count(x=>x.State==RuleState.Required)} olsun";
        StudioBlockedCount.Text=$"{MonumentItems.Count(x=>x.State==RuleState.Blocked)} olmasın";
        StudioCustomPrefabCount.Text=$"{MonumentItems.Count(x=>x.UseCustomPrefab)} özel görünüm";
        UpdateFilterCount();
    }

    private void CycleRule_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not MonumentRuleViewModel vm) return;
        vm.Cycle();
        MonumentView.Refresh();
        UpdateSummaries();
        ScheduleSave();
    }

    private void SetRuleState_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not MonumentRuleViewModel vm) return;
        vm.State = Convert.ToString(button.CommandParameter) switch
        {
            "Required" => RuleState.Required,
            "Blocked" => RuleState.Blocked,
            _ => RuleState.Optional
        };
        MonumentView.Refresh();
        UpdateSummaries();
        ScheduleSave();
    }

    private void WorldFeature_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || MonumentItems.Count == 0) return;
        UpdateSummaries();
        ScheduleSave();
    }
    private void GodRockMinus_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is MonumentRuleViewModel vm && vm.IsGodRock) { vm.TargetCount--; UpdateSummaries(); ScheduleSave(); } }
    private void GodRockPlus_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is MonumentRuleViewModel vm && vm.IsGodRock) { vm.TargetCount++; UpdateSummaries(); ScheduleSave(); } }
    private void MonumentItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (e.PropertyName == nameof(MonumentRuleViewModel.SelectedPrefabVariantId))
        {
            if (sender is MonumentRuleViewModel vm)
                TopStatus.Text = string.IsNullOrWhiteSpace(vm.SelectedPrefabVariantId)
                    ? $"● {vm.Name}: Vanilla"
                    : $"● {vm.Name}: {vm.PrefabModeText}";
            UpdateSummaries();
            ScheduleSave();
        }
    }

    private async void RefreshCustomPrefabs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var check=await App.LicenseService.RequireOnlineEntitlementAsync();
            if (!check.IsValid)
            {
                if (check.State == LicenseCheckState.ServerUnavailable) TopStatus.Text=check.Message;
                else await RevokeOpenSessionAsync(check);
                return;
            }
            var selections = MonumentItems.ToDictionary(x => x.Id, x => x.SelectedPrefabVariantId);
            await App.LicenseService.RefreshPrefabCatalogAsync();
            viewModel.RefreshCustomPrefabVariants();
            TopStatus.Text = "Yetkili özel prefab listesi yenilendi";
            if (MonumentItems.Any(x => selections.TryGetValue(x.Id, out var previous) && previous != x.SelectedPrefabVariantId))
                ScheduleSave();
        }
        catch (Exception ex) { TopStatus.Text="Prefab listesi alınamadı: "+ex.Message; }
    }

    private void AdvancedValue_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BiomeSummaryText is null || TierSummaryText is null) return;
        UpdateAdvancedLabels();
        if (IsLoaded) ScheduleSave();
    }

    private void UpdateAdvancedLabels()
    {
        if (BiomeAridValue is null || Tier0Value is null) return;
        static string P(double v) => $"{Math.Round(v):0}%";
        BiomeAridValue.Text = P(BiomeAridSlider.Value);
        BiomeTemperateValue.Text = P(BiomeTemperateSlider.Value);
        BiomeTundraValue.Text = P(BiomeTundraSlider.Value);
        BiomeArcticValue.Text = P(BiomeArcticSlider.Value);
        BiomeJungleValue.Text = P(BiomeJungleSlider.Value);
        Tier0Value.Text = P(Tier0Slider.Value);
        Tier1Value.Text = P(Tier1Slider.Value);
        Tier2Value.Text = P(Tier2Slider.Value);
        var mainBiome = BiomeAridSlider.Value + BiomeTemperateSlider.Value + BiomeTundraSlider.Value + BiomeArcticSlider.Value;
        var tierTotal = Tier0Slider.Value + Tier1Slider.Value + Tier2Slider.Value;

        static double Share(double value, double total) => total > 0.0001 ? value / total * 100d : 0d;
        var aridShare = Share(BiomeAridSlider.Value, mainBiome);
        var temperateShare = Share(BiomeTemperateSlider.Value, mainBiome);
        var tundraShare = Share(BiomeTundraSlider.Value, mainBiome);
        var arcticShare = Share(BiomeArcticSlider.Value, mainBiome);
        var tier0Share = Share(Tier0Slider.Value, tierTotal);
        var tier1Share = Share(Tier1Slider.Value, tierTotal);
        var tier2Share = Share(Tier2Slider.Value, tierTotal);

        BiomeSummaryText.Text =
            $"Çöl %{aridShare:0}  ·  Ilıman %{temperateShare:0}  ·  Tundra %{tundraShare:0}  ·  Kar %{arcticShare:0}  |  Jungle %{BiomeJungleSlider.Value:0} ayrı";
        TierSummaryText.Text =
            $"Tier 0 %{tier0Share:0}  ·  Tier 1 %{tier1Share:0}  ·  Tier 2 %{tier2Share:0}";
    }

    private void ResetBiome_Click(object sender, RoutedEventArgs e)
    {
        BiomeAridSlider.Value = 40;
        BiomeTemperateSlider.Value = 15;
        BiomeTundraSlider.Value = 15;
        BiomeArcticSlider.Value = 30;
        BiomeJungleSlider.Value = 70;
        UpdateAdvancedLabels();
        ScheduleSave();
        TopStatus.Text = "Biyom dağılımı varsayılana döndürüldü";
    }

    private void ResetTier_Click(object sender, RoutedEventArgs e)
    {
        Tier0Slider.Value = 30;
        Tier1Slider.Value = 30;
        Tier2Slider.Value = 40;
        UpdateAdvancedLabels();
        ScheduleSave();
        TopStatus.Text = "Tier dağılımı varsayılana döndürüldü";
    }
    private void ResetRules_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in MonumentItems) { vm.State = RuleState.Optional; vm.SelectedPrefabVariantId = ""; }
        selectedStateFilter = "Tümü";
        if (StateFilterBox is not null) StateFilterBox.SelectedIndex = 0;
        MonumentView.Refresh();
        UpdateSummaries();
        ScheduleSave();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        searchText = ((TextBox)sender).Text.Trim();
        if (sender == MonumentSearch && MonumentSearch.Text != searchText) MonumentSearch.Text = searchText;
        MonumentView.Refresh();
        UpdateFilterCount();
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        selectedCategory = Convert.ToString(CategoryBox.SelectedItem) ?? "Tümü";
        MonumentView.Refresh();
        UpdateFilterCount();
    }

    private void StateFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        selectedStateFilter = Convert.ToString(StateFilterBox.SelectedItem) ?? "Tümü";
        MonumentView.Refresh();
        UpdateFilterCount();
    }

    private void UpdateFilterCount()
    {
        if (FilterCountText is null) return;
        FilterCountText.Text = $"{MonumentView.Cast<object>().Count()} / {MonumentItems.Count}";
    }

    private void ScheduleSave()
    {
        if (suppressProjectChangeTracking) return;
        saveTimer.Stop();
        saveTimer.Start();
    }
}
