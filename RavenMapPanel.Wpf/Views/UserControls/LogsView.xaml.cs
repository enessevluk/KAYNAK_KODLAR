using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace RavenMapPanel.Views.UserControls
{
    public partial class LogsView : UserControl
    {
        public LogsView()
        {
            InitializeComponent();
            LoadLogs();
        }

        private void LoadLogs()
        {
            // In a real implementation, this would load logs from the server
            // For now, we'll just add some sample data
            var logs = new[]
            {
                new { Timestamp = new System.DateTime(2026, 8, 20, 14, 30, 0), Level = "Bilgi", Source = "Server", Message = "Sunucu başlatıldı" },
                new { Timestamp = new System.DateTime(2026, 8, 20, 14, 31, 5), Level = "Uyarı", Source = "Security", Message = "Yeni bağlantı algılandı" },
                new { Timestamp = new System.DateTime(2026, 8, 20, 14, 32, 10), Level = "Hata", Source = "Database", Message = "Veritabanı bağlantısı başarısız oldu" },
                new { Timestamp = new System.DateTime(2026, 8, 20, 14, 35, 15), Level = "Bilgi", Source = "Server", Message = "Yeni kullanıcı oluşturuldu: user1" },
                new { Timestamp = new System.DateTime(2026, 8, 20, 14, 40, 20), Level = "Bilgi", Source = "Update", Message = "Güncelleme 1.2.3 başarıyla yüklendi" }
            };

            LogsDataGrid.ItemsSource = logs;
        }

        private void RefreshLogsButton_Click(object sender, RoutedEventArgs e)
        {
            StatusTextBlock.Text = "Günlükler yenileniyor...";
            // Simulate refreshing logs
            System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    LoadLogs();
                    StatusTextBlock.Text = "Günlükler başarıyla yenilendi";
                });
            });
        }

        private void ExportLogsButton_Click(object sender, RoutedEventArgs e)
        {
            // In a real implementation, this would export logs to a file
            MessageBox.Show("Günlükleri dışa aktarma işlevi henüz implemented değil.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Tüm günlükleri temizlemek istediğinize emin misiniz?", "Temizle",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                // In a real implementation, this would clear logs from the server
                MessageBox.Show("Günlükler başarıyla temizlendi!", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                
                // Clear logs display
                LogsDataGrid.ItemsSource = null;
                StatusTextBlock.Text = "Günlükler temizlendi";
            }
        }
    }
}
