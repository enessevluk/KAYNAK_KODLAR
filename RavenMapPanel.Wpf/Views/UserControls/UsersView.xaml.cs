using System.Windows;
using System.Windows.Controls;

namespace RavenMapPanel.Views.UserControls
{
    public partial class UsersView : System.Windows.Controls.UserControl
    {
        public UsersView()
        {
            InitializeComponent();
            LoadUsers();
        }

        private void LoadUsers()
        {
            // In a real implementation, this would load users from the server
            // For now, we'll just add some sample data
            var users = new[]
            {
                new { Username = "admin", Email = "admin@ravenmap.com", Role = "Yönetici", Status = "Aktif", LastLogin = new System.DateTime(2026, 8, 20, 14, 30, 0) },
                new { Username = "user1", Email = "user1@ravenmap.com", Role = "Kullanıcı", Status = "Aktif", LastLogin = new System.DateTime(2026, 8, 19, 9, 15, 0) },
                new { Username = "observer", Email = "observer@ravenmap.com", Role = "Gözlemci", Status = "Pasif", LastLogin = new System.DateTime(2026, 8, 15, 16, 45, 0) }
            };

            UsersDataGrid.ItemsSource = users;
        }

        private void CreateUserButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(UsernameTextBox.Text))
            {
                MessageBox.Show("Lütfen kullanıcı adı girin!", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // In a real implementation, this would create a user on the server
            MessageBox.Show($"Yeni kullanıcı oluşturuldu: {UsernameTextBox.Text}", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
            
            // Clear form
            UsernameTextBox.Clear();
            EmailTextBox.Clear();
            PasswordBox.Clear();
            RoleComboBox.SelectedIndex = 0;
            
            // Reload users
            LoadUsers();
        }

        private void EditUserButton_Click(object sender, RoutedEventArgs e)
        {
            if (UsersDataGrid.SelectedItem == null)
            {
                MessageBox.Show("Lütfen bir kullanıcı seçin!", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // In a real implementation, this would open an edit dialog
            MessageBox.Show("Kullanıcı düzenleme işlevi henüz implemented değil.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void DeleteUserButton_Click(object sender, RoutedEventArgs e)
        {
            if (UsersDataGrid.SelectedItem == null)
            {
                MessageBox.Show("Lütfen bir kullanıcı seçin!", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show("Seçili kullanıcıyı silmek istediğinize emin misiniz?", "Sil",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                // In a real implementation, this would delete the user from the server
                MessageBox.Show("Kullanıcı başarıyla silindi!", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                
                // Reload users
                LoadUsers();
            }
        }
    }
}