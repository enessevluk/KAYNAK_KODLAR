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
    private void Navigate_Click(object sender, RoutedEventArgs e) => Navigate(Convert.ToString(((Button)sender).Tag) ?? "Dashboard");
    private void GoStudio_Click(object sender, RoutedEventArgs e) => Navigate("Studio");
    private void GoMonuments_Click(object sender, RoutedEventArgs e) => Navigate("Monuments");
    private void GoAdvanced_Click(object sender, RoutedEventArgs e) => Navigate("Advanced");
    private void Navigate(string page)
    {
        DashboardPage.Visibility = page == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        StudioPage.Visibility = page == "Studio" ? Visibility.Visible : Visibility.Collapsed;
        MonumentsPage.Visibility = page == "Monuments" ? Visibility.Visible : Visibility.Collapsed;
        IconsPage.Visibility = page == "Icons" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility = page == "Advanced" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "History" ? Visibility.Visible : Visibility.Collapsed;
        ValidationPage.Visibility = page == "Validation" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = page == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "History") RefreshHistory();

        var navButtons = new[] { NavDashboard, NavStudio, NavMonuments, NavIcons, NavAdvanced, NavHistory, NavValidation, NavLogs, NavSettings };
        foreach (var button in navButtons)
        {
            var active = string.Equals(Convert.ToString(button.Tag), page, StringComparison.OrdinalIgnoreCase);
            button.Background = active
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x1B, 0x19))
                : System.Windows.Media.Brushes.Transparent;
            button.BorderBrush = active
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0x32, 0x1F))
                : System.Windows.Media.Brushes.Transparent;
            button.Foreground = active
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xF2, 0xEB))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8F, 0x9C, 0xAD));
        }

        BottomPageStatus.Text = page switch
        {
            "Dashboard" => "Başlangıç",
            "Studio" => "Harita Studio",
            "Monuments" => "Monumentler",
            "Icons" => "Harita Görünümü",
            "Advanced" => "Dünya ve Biyomlar",
            "History" => "Üretim Kütüphanesi",
            "Validation" => "Üretim Kontrolü",
            "Settings" => "Ayarlar",
            _ => "Aktivite"
        };
    }

    private void PreviousHero_Click(object sender,RoutedEventArgs e) => ChangeHeroManually(-1);
    private void NextHero_Click(object sender,RoutedEventArgs e) => ChangeHeroManually(1);
    private void ChangeHeroManually(int direction)
    {
        heroTimer.Stop();
        StepHero(direction);
        heroTimer.Start();
    }

    private void StepHero(int direction)
    {
        var count=heroMediaUrls.Count>0?heroMediaUrls.Count:heroBackgrounds.Length;
        heroIndex=(heroIndex+direction+count)%count;
        UpdateHeroBackground();
    }

    private async void UpdateHeroBackground()
    {
        var loadVersion=++heroLoadVersion;
        var count=heroMediaUrls.Count>0?heroMediaUrls.Count:heroBackgrounds.Length;
        heroIndex=Math.Clamp(heroIndex,0,count-1);

        if (heroMediaUrls.Count>0)
        {
            var loaded=await DashboardHeroImage.LoadHttpsAsync(heroMediaUrls[heroIndex]);
            if (loadVersion!=heroLoadVersion) return;
            if (loaded)
                return;
        }

        var localIndex=heroIndex%heroBackgrounds.Length;
        DashboardHeroImage.SetStaticSource(new BitmapImage(new Uri($"pack://application:,,,/Assets/backgrounds/{heroBackgrounds[localIndex]}",UriKind.Absolute)));
    }
}
