using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RavenMapPanel;

public partial class LicenseWindow : Window
{
    private readonly LicenseService service;

    public LicenseWindow(LicenseService service, LicenseCheckResult? initialResult = null)
    {
        InitializeComponent();
        this.service = service;

        DeviceIdText.Text = service.LicenseServerId;
        CenterAddressText.Text = "Lisans merkezi: " + service.ServerConfig.GetLicenseCenterUri().GetLeftPart(UriPartial.Authority);
        StoreButton.Visibility = LicenseService.NormalizeExternalUrl(service.ServerConfig.StoreUrl) is null ? Visibility.Collapsed : Visibility.Visible;
        SupportButton.Visibility = LicenseService.NormalizeExternalUrl(service.ServerConfig.SupportUrl) is null ? Visibility.Collapsed : Visibility.Visible;

        if (initialResult is not null)
            SetStatus(initialResult.Message, initialResult.State == LicenseCheckState.ServerUnavailable ? "#F29A48" : "#F06464");

        Loaded += (_, _) => ActivationKeyBox.Focus();
    }

    private void SetStatus(string message, string color)
    {
        StatusText.Text = message;
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        StatusText.Foreground = brush;
        StatusDot.Fill = brush;
    }

    private async void Activate_Click(object sender, RoutedEventArgs e) => await ActivateAsync();

    private async Task ActivateAsync()
    {
        if (!ActivateButton.IsEnabled) return;
        ActivateButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        SetStatus("Lisans merkeziyle bağlantı kuruluyor...", "#F29A48");
        try
        {
            var result = await service.ActivateLicenseKeyAsync(ActivationKeyBox.Password);
            if (!result.IsValid)
            {
                SetStatus(result.Message, result.State == LicenseCheckState.ServerUnavailable ? "#F29A48" : "#F06464");
                return;
            }

            ActivationKeyBox.Clear();
            SetStatus("✓ " + result.Message + " Raven açılıyor...", "#5FD39A");
            await Task.Delay(180);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            SetStatus("Lisans etkinleştirilemedi: " + ex.Message, "#F06464");
        }
        finally
        {
            ActivateButton.IsEnabled = true;
            RetryButton.IsEnabled = true;
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        ActivateButton.IsEnabled = false;
        SetStatus("Kayıtlı lisans çevrimiçi kontrol ediliyor...", "#F29A48");
        try
        {
            var result = await service.RequireOnlineEntitlementAsync();
            if (!result.IsValid)
            {
                SetStatus(result.Message, result.State == LicenseCheckState.ServerUnavailable ? "#F29A48" : "#F06464");
                return;
            }

            SetStatus("✓ Lisans doğrulandı. Raven açılıyor...", "#5FD39A");
            await Task.Delay(180);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            SetStatus("Lisans kontrolü tamamlanamadı: " + ex.Message, "#F06464");
        }
        finally
        {
            RetryButton.IsEnabled = true;
            ActivateButton.IsEnabled = true;
        }
    }

    private async void ActivationKeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ActivateAsync();
    }

    private void CopyDevice_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(service.LicenseServerId);
            SetStatus("Cihaz kimliği panoya kopyalandı.", "#55A8FF");
        }
        catch
        {
            SetStatus("Cihaz kimliği panoya kopyalanamadı.", "#F06464");
        }
    }

    private void Store_Click(object sender, RoutedEventArgs e) => OpenConfiguredUrl(service.ServerConfig.StoreUrl, "Satın alma");
    private void Support_Click(object sender, RoutedEventArgs e) => OpenConfiguredUrl(service.ServerConfig.SupportUrl, "Destek");

    private void OpenConfiguredUrl(string? url, string label)
    {
        if (string.IsNullOrWhiteSpace(url) || !service.OpenStore(url))
            SetStatus(label + " bağlantısı açılamadı.", "#F06464");
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
