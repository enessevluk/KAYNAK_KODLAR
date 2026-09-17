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
    private void OnMapMarkerSelected(MapMarker? marker)
    {
        selectedMapMarker = marker;
        if (marker is null)
        {
            MarkerDetailsCard.Visibility = Visibility.Collapsed;
            return;
        }

        var rule = catalog.FirstOrDefault(x => x.Id.Equals(marker.Category, StringComparison.OrdinalIgnoreCase));
        markerGallery = MonumentGalleryService.Load(rule);
        markerGalleryIndex = 0;
        if (markerGallery.Count == 0)
        {
            MarkerDetailsCard.Visibility = Visibility.Collapsed;
            return;
        }
        UpdateMarkerGallery();
        MarkerDetailsCard.Visibility = Visibility.Visible;
    }

    private void UpdateMarkerGallery()
    {
        if (markerGallery.Count == 0) return;
        markerGalleryIndex = Math.Clamp(markerGalleryIndex, 0, markerGallery.Count - 1);
        var item = markerGallery[markerGalleryIndex];
        MarkerGalleryImage.Source = item.Source;
        MarkerGalleryImage.Stretch = Stretch.Uniform;
        var canNavigate = markerGallery.Count > 1;
        MarkerGalleryPrevious.Visibility = canNavigate ? Visibility.Visible : Visibility.Collapsed;
        MarkerGalleryNext.Visibility = canNavigate ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MarkerGalleryPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (markerGallery.Count < 2) return;
        markerGalleryIndex = (markerGalleryIndex - 1 + markerGallery.Count) % markerGallery.Count;
        UpdateMarkerGallery();
    }

    private void MarkerGalleryNext_Click(object sender, RoutedEventArgs e)
    {
        if (markerGallery.Count < 2) return;
        markerGalleryIndex = (markerGalleryIndex + 1) % markerGallery.Count;
        UpdateMarkerGallery();
    }

    private void CloseMarkerDetails_Click(object sender, RoutedEventArgs e) => MapView.ClearSelection();
}
