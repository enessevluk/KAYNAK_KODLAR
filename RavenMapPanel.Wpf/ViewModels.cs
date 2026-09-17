using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace RavenMapPanel;

public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record CustomPrefabOption(string Id, string Name, string PreviewPath, string StatusText, bool IsValid)
{
    public static readonly CustomPrefabOption Vanilla = new("", "Vanilla", "", "Rust'ın orijinal monument görünümü", true);

    // Custom ComboBox template seçili kayıtta ToString() kullandığı için
    // "CustomPrefabOption {...}" yerine doğrudan kullanıcıya görünen adı döndür.
    public override string ToString() => Name;
}

public sealed class MonumentRuleViewModel : NotifyBase
{
    public MonumentRule Rule { get; }
    public string Id => Rule.Id;
    public string Name => Rule.Name;
    public string Category => Rule.Category;
    public string IconPath => AssetStore.IconSource(Rule.Icon);
    public string SourceLabel => Rule.IsCustom ? "Özel" : "Raven";
    public bool IsGodRock => string.Equals(Id, "god_rocks", StringComparison.OrdinalIgnoreCase);
    public bool CustomPrefabAvailable => Rule.CustomPrefabAvailable;
    public ObservableCollection<CustomPrefabOption> PrefabOptions { get; } = [CustomPrefabOption.Vanilla];

    public string SelectedPrefabVariantId
    {
        get => Rule.CustomPrefabVariantId;
        set
        {
            var next = (value ?? "").Trim();
            if (string.Equals(Rule.CustomPrefabVariantId, next, StringComparison.OrdinalIgnoreCase)) return;
            Rule.CustomPrefabVariantId = next;
            Changed();
            Changed(nameof(UseCustomPrefab));
            Changed(nameof(PrefabModeText));
            Changed(nameof(PrefabModeColor));
            Changed(nameof(SelectedPrefabOption));
            Changed(nameof(PrefabStatusText));
            Changed(nameof(PrefabPreviewPath));
        }
    }

    public bool UseCustomPrefab => !string.IsNullOrWhiteSpace(SelectedPrefabVariantId);
    public CustomPrefabOption SelectedPrefabOption => PrefabOptions.FirstOrDefault(x => x.Id.Equals(SelectedPrefabVariantId, StringComparison.OrdinalIgnoreCase)) ?? CustomPrefabOption.Vanilla;
    public string PrefabModeText => SelectedPrefabOption.Name;
    public string PrefabModeColor => UseCustomPrefab ? "#62D8BD" : "#8A9BAB";
    public string PrefabStatusText => SelectedPrefabOption.StatusText;
    public int AvailableCustomPrefabCount => Math.Max(0, PrefabOptions.Count - 1);
    public string PrefabAvailabilityText => AvailableCustomPrefabCount switch
    {
        0 => "Bu plan için özel görünüm yok",
        1 => "1 özel görünüm mevcut · listeden seçebilirsiniz",
        _ => $"{AvailableCustomPrefabCount} özel görünüm mevcut · listeden seçebilirsiniz"
    };
    public string PrefabAvailabilityColor => AvailableCustomPrefabCount > 0 ? "#78C9E8" : "#718190";
    public string PrefabPreviewPath => SelectedPrefabOption.PreviewPath;
    public bool HasPrefabPreview => !string.IsNullOrWhiteSpace(PrefabPreviewPath);

    public int TargetCount
    {
        get => IsGodRock ? Math.Clamp(Rule.Minimum > 0 ? Rule.Minimum : 3, 1, 10) : Rule.Minimum;
        set
        {
            if (!IsGodRock) return;
            var next = Math.Clamp(value, 1, 10);
            if (Rule.Minimum == next && Rule.Maximum == next) return;
            Rule.Minimum = next;
            Rule.Maximum = next;
            Changed();
        }
    }

    public RuleState State
    {
        get => Rule.State;
        set
        {
            if (Rule.State == value) return;
            Rule.State = value;
            if (value == RuleState.Required && Rule.Minimum == 0)
            {
                Rule.Minimum = IsGodRock ? 3 : 1;
                if (IsGodRock) Rule.Maximum = Rule.Minimum;
            }
            Changed();
            Changed(nameof(StateSymbol));
            Changed(nameof(StateText));
            Changed(nameof(StateColor));
            Changed(nameof(StateBackground));
            Changed(nameof(StateBorderColor));
            Changed(nameof(IsRequired));
            Changed(nameof(IsOptional));
            Changed(nameof(IsBlocked));
            Changed(nameof(TargetCount));
            Changed(nameof(PrefabStatusText));
        }
    }

    public bool IsRequired => State == RuleState.Required;
    public bool IsOptional => State == RuleState.Optional;
    public bool IsBlocked => State == RuleState.Blocked;
    public string StateSymbol => State switch { RuleState.Required => "✓", RuleState.Blocked => "×", _ => "?" };
    public string StateText => State switch { RuleState.Required => IsGodRock ? "Kesin olsun" : "Olsun", RuleState.Blocked => "Olmasın", _ => "İsteğe bağlı" };
    public string StateColor => State switch { RuleState.Required => "#59E39A", RuleState.Blocked => "#FF737A", _ => "#FFD35A" };
    public string StateBackground => State switch { RuleState.Required => "#173B2D", RuleState.Blocked => "#401F25", _ => "#403719" };
    public string StateBorderColor => State switch { RuleState.Required => "#2C9B65", RuleState.Blocked => "#B84B54", _ => "#B49532" };

    public MonumentRuleViewModel(MonumentRule rule) => Rule = rule;

    public void SetPrefabVariants(IEnumerable<CustomPrefabVariant> variants, string? selectedVariantId = null)
    {
        var wanted = selectedVariantId ?? SelectedPrefabVariantId;
        PrefabOptions.Clear();
        PrefabOptions.Add(CustomPrefabOption.Vanilla);
        foreach (var variant in variants.Where(x => x.IsValid))
        {
            PrefabOptions.Add(new CustomPrefabOption(
                variant.Id,
                variant.Name,
                variant.PreviewPath,
                variant.ValidationMessage,
                variant.IsValid));
        }

        var selected = PrefabOptions.FirstOrDefault(
            x => x.Id.Equals(wanted ?? "", StringComparison.OrdinalIgnoreCase));

        // Eski v1.4.x proje seçimi "custom/custom1" ise ilk gerçek dosyaya migrate et.
        if (selected is null &&
            !string.IsNullOrWhiteSpace(wanted) &&
            (wanted.Equals("custom", StringComparison.OrdinalIgnoreCase) ||
             wanted.Equals("custom1", StringComparison.OrdinalIgnoreCase)))
        {
            selected = PrefabOptions.Skip(1).FirstOrDefault();
        }

        Rule.CustomPrefabVariantId = selected?.Id ?? "";
        NotifyRuleValuesChanged();
    }

    public void NotifyRuleValuesChanged()
    {
        Changed(nameof(TargetCount));
        Changed(nameof(SelectedPrefabVariantId));
        Changed(nameof(UseCustomPrefab));
        Changed(nameof(PrefabModeText));
        Changed(nameof(PrefabModeColor));
        Changed(nameof(SelectedPrefabOption));
        Changed(nameof(PrefabStatusText));
        Changed(nameof(AvailableCustomPrefabCount));
        Changed(nameof(PrefabAvailabilityText));
        Changed(nameof(PrefabAvailabilityColor));
        Changed(nameof(PrefabPreviewPath));
        Changed(nameof(HasPrefabPreview));
    }

    public void Cycle() => State = State switch
    {
        RuleState.Optional => RuleState.Required,
        RuleState.Required => RuleState.Blocked,
        _ => RuleState.Optional
    };
}

public sealed record MapMarker(string Category, string Name, string Source, double X, double Z, string IconFile);

public sealed record TerrainSafetyIssueView(string Severity, string Name, double X, double Z, double TerrainRange, string Message);

public sealed class TerrainSafetyView
{
    public bool Enabled { get; init; }
    public string Mode { get; init; } = "disabled";
    public int BudgetMs { get; init; }
    public long ElapsedMs { get; init; }
    public int Candidates { get; init; }
    public int Checked { get; init; }
    public int Protected { get; init; }
    public bool BudgetExceeded { get; init; }
    public bool HeightMapAvailable { get; init; }
    public bool AlphaMapAvailable { get; init; }
    public int SlopeObservations { get; init; }
    public bool SafeAutoRepairEnabled { get; init; }
    public int RepairsApplied { get; init; }
    public int RepairCandidates { get; init; }
    public List<TerrainSafetyIssueView> Issues { get; init; } = [];
}

public sealed class MapDocument
{
    public string ImagePath { get; init; } = "";
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public int WorldSize { get; init; }
    public int MapResolution { get; init; }
    public int RenderOffset { get; init; }
    public string ReportFolder { get; init; } = "";
    public int Seed { get; init; }
    public TerrainSafetyView? TerrainSafety { get; init; }
    public ObservableCollection<MapMarker> Markers { get; } = [];
    public BitmapImage? Image { get; set; }

    public System.Windows.Point Project(double x, double z)
    {
        var px = RenderOffset + ((x + WorldSize / 2d) / WorldSize) * MapResolution;
        var py = RenderOffset + ((WorldSize / 2d - z) / WorldSize) * MapResolution;
        return new(px * ImageWidth / Math.Max(1d, ImageWidth), py * ImageHeight / Math.Max(1d, ImageHeight));
    }
}
