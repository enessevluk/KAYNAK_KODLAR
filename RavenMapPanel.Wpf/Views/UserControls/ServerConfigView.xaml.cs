using System.Windows;
using System.Windows.Controls;

namespace RavenMapPanel.Views.UserControls
{
    public partial class ServerConfigView : System.Windows.Controls.UserControl
    {
        public ServerConfigView()
        {
            InitializeComponent();
            LoadConfiguration();
        }

        private void LoadConfiguration()
        {
            // In a real implementation, this would load the configuration from the server
            // For now, we'll just set some sample values
            ServerNameTextBox.Text = "RavenMAP Server";
            PortTextBox.Text = "8080";
            AdminUsernameTextBox.Text = "admin";
            EnableShopierPaymentsCheckBox.IsChecked = false;
        }

        private void SaveConfigButton_Click(object sender, RoutedEventArgs e)
        {
            // In a real implementation, this would save the configuration to the server
            MessageBox.Show("Ayarlar başarıyla kaydedildi!", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ResetConfigButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Tüm ayarları sıfırlamak istediğinize emin misiniz?", "Sıfırla", 
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                LoadConfiguration();
            }
        }
    }
}