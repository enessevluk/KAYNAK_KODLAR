using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UserControl = System.Windows.Controls.UserControl;

namespace RavenMapPanel.Views
{
    public partial class ModernAdminView : UserControl
    {
        public ModernAdminView()
        {
            InitializeComponent();
            NavigateToView("ServerConfig");
        }

        private void NavigateToView(string viewName)
        {
            UserControl content;

            switch (viewName)
            {
                case "ServerConfig":
                    content = new UserControls.ServerConfigView();
                    break;
                case "Updates":
                    content = new UserControls.UpdatesView();
                    break;
                case "Users":
                    content = new UserControls.UsersView();
                    break;
                case "Logs":
                    content = new UserControls.LogsView();
                    break;
                default:
                    content = new UserControls.ServerConfigView();
                    break;
            }

            ContentFrame.Content = content;
            UpdateActiveNavigation(viewName);
        }

        private void UpdateActiveNavigation(string viewName)
        {
            var buttons = new[] { ServerConfigButton, UpdatesButton, UsersButton, LogsButton };

            foreach (var button in buttons)
            {
                var isActive = string.Equals(button.Tag?.ToString(), viewName, System.StringComparison.Ordinal);
                button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isActive ? "#222622" : "#00000000"));
                button.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isActive ? "#FFFFFF" : "#9DA49D"));
                button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isActive ? "#D85C36" : "#00000000"));
                button.BorderThickness = isActive ? new Thickness(3, 0, 0, 0) : new Thickness(0);
            }
        }

        private void ServerConfigButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToView("ServerConfig");
        }

        private void UpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToView("Updates");
        }

        private void UsersButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToView("Users");
        }

        private void LogsButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToView("Logs");
        }
    }
}
