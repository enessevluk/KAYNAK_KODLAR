using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace RavenMapPanel.Views.UserControls
{
    public partial class UpdatesView : UserControl
    {
        public UpdatesView()
        {
            InitializeComponent();
            LoadUpdates();
        }

        private void LoadUpdates()
        {
            // In a real implementation, this would load updates from the server
            // For now, we'll just add some sample data
            var updates = new[]
            {
                new { Version = "1.2.3", ReleaseDate = new System.DateTime(2026, 8, 15), Size = 1024 * 1024 * 50, Description = "Critical security patch" },
                new { Version = "1.2.2", ReleaseDate = new System.DateTime(2026, 7, 20), Size = 1024 * 1024 * 45, Description = "Bug fixes and performance improvements" },
                new { Version = "1.2.1", ReleaseDate = new System.DateTime(2026, 6, 10), Size = 1024 * 1024 * 40, Description = "New features and UI enhancements" }
            };

            UpdatesDataGrid.ItemsSource = updates;
        }

        private void CreateUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(VersionTextBox.Text))
            {
                MessageBox.Show("Lütfen sürüm numarası girin!", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // In a real implementation, this would create an update on the server
            MessageBox.Show($"Yeni güncelleme oluşturuldu: {VersionTextBox.Text}", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
            
            // Clear form
            VersionTextBox.Clear();
            DescriptionTextBox.Clear();
            
            // Reload updates
            LoadUpdates();
        }

        private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            StatusTextBlock.Text = "Güncellemeler kontrol ediliyor...";
            // Simulate checking for updates
            System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusTextBlock.Text = "Yeni güncelleme mevcut: 1.2.4";
                });
            });
        }

        private void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (UpdatesDataGrid.SelectedItem == null)
            {
                MessageBox.Show("Lütfen bir güncelleme seçin!", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StatusTextBlock.Text = "Güncelleme indiriliyor...";
            // Simulate download and install
            System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusTextBlock.Text = "Güncelleme başarıyla yüklendi!";
                    MessageBox.Show("Güncelleme başarıyla yüklendi!", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            });
        }

        private void RollbackUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (UpdatesDataGrid.SelectedItem == null)
            {
                MessageBox.Show("Lütfen bir güncelleme seçin!", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show("Bu işlem geri alınamaz. Geri almak istediğinize emin misiniz?", "Geri Alma",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                StatusTextBlock.Text = "Geri alma işlemi yapılıyor...";
                // Simulate rollback
                System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        StatusTextBlock.Text = "Geri alma işlemi tamamlandı!";
                        MessageBox.Show("Geri alma işlemi tamamlandı!", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                    });
                });
            }
        }
    }
}
