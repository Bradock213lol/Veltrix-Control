using System.Windows;
using System.Windows.Controls;
using System.Net.Http;
using VeltrixControl.Contracts;

namespace VeltrixControl.Desktop;

public partial class EnrollmentWindow : Window
{
    private readonly ControllerApiClient _api;
    private EnrollmentTokenResponse? _token;

    public EnrollmentWindow(ControllerApiClient api)
    {
        InitializeComponent();
        _api = api;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        CreateButton.IsEnabled = false;
        StatusText.Text = string.Empty;
        try
        {
            if (_token is not null)
            {
                await _api.RevokeEnrollmentTokenAsync(_token.Id);
                _token = null;
                CodeBox.Text = "Previous code revoked";
                ExpiryText.Text = string.Empty;
                CopyButton.IsEnabled = RevokeButton.IsEnabled = false;
            }
            var lifetime = int.TryParse((LifetimeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var value) ? value : 15;
            _token = await _api.CreateEnrollmentTokenAsync(lifetime);
            CodeBox.Text = _token.Code;
            ExpiryText.Text = $"Expires {_token.ExpiresAt.LocalDateTime:F}";
            CopyButton.IsEnabled = RevokeButton.IsEnabled = true;
            Clipboard.SetText(_token.Code);
            StatusText.Text = "Created and copied.";
        }
        catch (Exception exception) when (exception is ControllerApiException or HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_token is null) return;
        Clipboard.SetText(_token.Code);
        StatusText.Text = "Copied.";
    }

    private async void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if (_token is null) return;
        RevokeButton.IsEnabled = false;
        try
        {
            await _api.RevokeEnrollmentTokenAsync(_token.Id);
            _token = null;
            CodeBox.Text = "Revoked";
            ExpiryText.Text = string.Empty;
            CopyButton.IsEnabled = false;
            StatusText.Text = "Code revoked.";
        }
        catch (Exception exception) when (exception is ControllerApiException or HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = exception.Message;
            RevokeButton.IsEnabled = true;
        }
    }
}
