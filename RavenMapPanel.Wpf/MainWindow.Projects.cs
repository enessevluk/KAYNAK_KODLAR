using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace RavenMapPanel;

public partial class MainWindow : Window
{
    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProjectChangesCanBeDiscarded()) return;

        settings = projectService.CreateNew(settings);
        SetPreviewExpanded(false);
        foreach (var vm in MonumentItems)
        {
            vm.Rule.Minimum = 0;
            vm.Rule.Maximum = 99;
            vm.State = RuleState.Optional;
            vm.SelectedPrefabVariantId = "";
        }
        LoadSettingsIntoUi();
        MarkProjectStateSaved();
        result = null;
        MapView.Document = null;
        MarkerDetailsCard.Visibility = Visibility.Collapsed;
        Navigate("Studio");
        TopStatus.Text = "Yeni harita ayarları hazır";
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Raven harita ayarları|*.ravenmap|JSON ayarları|*.json",
            InitialDirectory = SettingsStore.ConfigRoot
        };
        if (dialog.ShowDialog(this) == true)
            LoadProject(dialog.FileName);
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(settings.CurrentProjectPath))
            SaveProjectAs();
        else
            SaveProject(settings.CurrentProjectPath);
    }

    private void SaveProjectAs_Click(object sender, RoutedEventArgs e) => SaveProjectAs();

    private bool SaveProjectAs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Raven harita ayarı|*.ravenmap",
            InitialDirectory = SettingsStore.ConfigRoot,
            FileName = SettingsStore.SafeName(MapNameBox.Text, "RavenHarita") + ".ravenmap",
            AddExtension = true
        };
        return dialog.ShowDialog(this) == true && SaveProject(dialog.FileName);
    }

    private bool SaveProject(string path)
    {
        try
        {
            FlushPendingProjectChanges();
            projectService.Save(settings, path);
            LoadSettingsIntoUi();
            MarkProjectStateSaved();
            TopStatus.Text = "Harita ayarları kaydedildi";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Harita ayarları kaydedilemedi",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void LoadProject(string path)
    {
        if (!EnsureProjectChangesCanBeDiscarded()) return;

        try
        {
            settings = projectService.Load(path, settings);
            LoadSettingsIntoUi();
            MarkProjectStateSaved();
            Navigate("Studio");
            TopStatus.Text = "Harita ayarları yüklendi";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Harita ayarları yüklenemedi",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void WireProjectDirtyTracking()
    {
        if (projectDirtyTrackingWired) return;
        projectDirtyTrackingWired = true;

        MapNameBox.TextChanged += (_, _) => ScheduleSave();
        SeedBox.TextChanged += (_, _) => ScheduleSave();
        SizeBox.TextChanged += (_, _) => ScheduleSave();
        GenerationCountBox.SelectionChanged += (_, _) => ScheduleSave();

        CheckBox[] projectChecks =
        [
            RoadsCheck, RiversCheck, PowerCheck, RailsCheck, MetroCheck,
            OasisCheck, CanyonCheck, LakesCheck
        ];
        foreach (var check in projectChecks)
        {
            check.Checked += (_, _) => ScheduleSave();
            check.Unchecked += (_, _) => ScheduleSave();
        }
    }

    private void FlushPendingProjectChanges()
    {
        saveTimer.Stop();
        SyncUiToSettings();
        SettingsStore.Save(settings);
        RefreshProjectDirtyState();
    }

    private void MarkProjectStateSaved()
    {
        savedProjectFingerprint = projectService.Fingerprint(settings);
        projectDirty = false;
        UpdateProjectDirtyDisplay();
    }

    private void RefreshProjectDirtyState()
    {
        if (suppressProjectChangeTracking) return;
        projectDirty = !string.Equals(
            savedProjectFingerprint,
            projectService.Fingerprint(settings),
            StringComparison.Ordinal);
        UpdateProjectDirtyDisplay();
    }

    private void UpdateProjectDirtyDisplay()
    {
        var name = string.IsNullOrWhiteSpace(settings.MapName)
            ? "Yeni Raven Haritası"
            : settings.MapName;
        ProjectTitle.Text = name;
    }

    private bool EnsureProjectChangesCanBeDiscarded()
    {
        FlushPendingProjectChanges();
        if (!projectDirty) return true;

        var answer = MessageBox.Show(this,
            "Harita ayarlarında kaydedilmemiş değişiklikler var.\n\n" +
            "Ayarları kaydetmek ister misiniz?",
            "Kaydedilmemiş ayarlar",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) return true;
        return string.IsNullOrWhiteSpace(settings.CurrentProjectPath)
            ? SaveProjectAs()
            : SaveProject(settings.CurrentProjectPath);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!licenseAccessLost && !EnsureProjectChangesCanBeDiscarded())
        {
            e.Cancel = true;
            return;
        }

        licenseHeartbeatTimer.Stop();
        heroTimer.Stop();
        saveTimer.Stop();
        SyncUiToSettings();
        SettingsStore.Save(settings);
    }
}
