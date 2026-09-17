using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace RavenMapPanel;

internal sealed class MainWindowViewModel : NotifyBase
{
    private AppSettings settings;
    private string searchText = "";
    private string selectedCategory = "Tümü";
    private string selectedStateFilter = "Tümü";

    public List<MonumentRule> Catalog { get; }
    public CustomPrefabService CustomPrefabService { get; } = new();
    public ObservableCollection<MonumentRuleViewModel> MonumentItems { get; } = [];
    public ObservableCollection<MonumentRuleViewModel> IconItems { get; } = [];
    public IEnumerable<MonumentRuleViewModel> CustomPrefabItems => MonumentItems.Where(x => x.CustomPrefabAvailable);
    public ICollectionView MonumentView { get; }
    public ObservableCollection<GenerationHistoryEntry> GenerationHistoryItems { get; } = [];
    public ObservableCollection<MapComparisonRow> ComparisonItems { get; } = [];

    public AppSettings Settings
    {
        get => settings;
        set
        {
            settings = value;
            Changed();
        }
    }

    public GenerationResult? Result { get; set; }
    public CancellationTokenSource? GenerationCancellation { get; set; }
    public MapMarker? SelectedMapMarker { get; set; }

    public string SearchText
    {
        get => searchText;
        set
        {
            if (searchText == value) return;
            searchText = value;
            MonumentView.Refresh();
            Changed();
        }
    }

    public string SelectedCategory
    {
        get => selectedCategory;
        set
        {
            if (selectedCategory == value) return;
            selectedCategory = value;
            MonumentView.Refresh();
            Changed();
        }
    }

    public string SelectedStateFilter
    {
        get => selectedStateFilter;
        set
        {
            if (selectedStateFilter == value) return;
            selectedStateFilter = value;
            MonumentView.Refresh();
            Changed();
        }
    }

    public MainWindowViewModel()
    {
        settings = SettingsStore.Load();
        Catalog = AssetStore.LoadCatalog();
        CustomPrefabService.EnsureBundledDefaults();
        foreach (var rule in Catalog.OrderBy(x => x.Category).ThenBy(x => x.Name))
        {
            var item = new MonumentRuleViewModel(rule);
            item.SetPrefabVariants(CustomPrefabService.GetVariants(rule.Id));
            IconItems.Add(item);
            if (!rule.IsWorldFeature)
                MonumentItems.Add(item);
        }
        MonumentView = CollectionViewSource.GetDefaultView(MonumentItems);
        MonumentView.Filter = FilterRule;
    }

    public void RefreshCustomPrefabVariants()
    {
        CustomPrefabService.EnsureBundledDefaults();
        foreach (var vm in MonumentItems.Where(x => x.CustomPrefabAvailable))
        {
            var selected = vm.SelectedPrefabVariantId;
            vm.SetPrefabVariants(CustomPrefabService.GetVariants(vm.Id), selected);
        }
        Changed(nameof(CustomPrefabItems));
    }

    public void ResetFilters()
    {
        searchText = "";
        selectedCategory = "Tümü";
        selectedStateFilter = "Tümü";
        MonumentView.Refresh();
        Changed(nameof(SearchText));
        Changed(nameof(SelectedCategory));
        Changed(nameof(SelectedStateFilter));
    }

    private bool FilterRule(object item)
    {
        if (item is not MonumentRuleViewModel vm) return false;
        if (SelectedCategory != "Tümü" && vm.Category != SelectedCategory) return false;
        if (SelectedStateFilter == "Olsun" && vm.State != RuleState.Required) return false;
        if (SelectedStateFilter == "Olmasın" && vm.State != RuleState.Blocked) return false;
        if (SelectedStateFilter == "İsteğe bağlı" && vm.State != RuleState.Optional) return false;
        return SearchText.Length == 0 ||
               (vm.Name + " " + vm.Category).Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);
    }
}
