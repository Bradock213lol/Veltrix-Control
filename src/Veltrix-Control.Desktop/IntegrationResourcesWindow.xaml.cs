using System.Net.Http;
using System.Text.Json;
using System.Windows;
using VeltrixControl.Contracts;

namespace VeltrixControl.Desktop;

public partial class IntegrationResourcesWindow : Window
{
    private readonly ControllerApiClient _api;
    private readonly Guid _integrationId;

    public IntegrationResourcesWindow(ControllerApiClient api, IntegrationView integration)
    {
        InitializeComponent();
        _api = api;
        _integrationId = integration.Id;
        Title = $"Resources · {integration.Name}";
        Caption.Text = $"{integration.Name} · {integration.Kind}";
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var resources = await _api.GetIntegrationResourcesAsync(_integrationId);
            ResourcesGrid.ItemsSource = resources;
            Caption.Text = $"{Title.Replace("Resources · ", string.Empty, StringComparison.Ordinal)} · {resources.Length} resource(s)";
        }
        catch (Exception exception) when (exception is ControllerApiException or HttpRequestException or TaskCanceledException)
        {
            Caption.Text = exception.Message;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Start_Click(object sender, RoutedEventArgs e) => await ExecuteAsync("start");
    private async void Stop_Click(object sender, RoutedEventArgs e) => await ExecuteAsync("stop");
    private async void Restart_Click(object sender, RoutedEventArgs e) => await ExecuteAsync("restart");

    private async Task ExecuteAsync(string action)
    {
        if (ResourcesGrid.SelectedItem is not IntegrationResourceView resource) return;
        if (MessageBox.Show(this, $"{char.ToUpperInvariant(action[0]) + action[1..]} '{resource.Name}'?", "Integration action", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var result = await _api.ExecuteIntegrationActionAsync(_integrationId, action, resource.Id);
            Caption.Text = result.TryGetProperty("detail", out var detail) ? detail.GetString() ?? "Done." : "Done.";
            await LoadAsync();
        }
        catch (Exception exception) when (exception is ControllerApiException or HttpRequestException or TaskCanceledException)
        {
            Caption.Text = exception.Message;
        }
    }

    private async void Logs_Click(object sender, RoutedEventArgs e)
    {
        if (ResourcesGrid.SelectedItem is not IntegrationResourceView resource) return;
        try
        {
            LogsBox.Text = "Loading logs…";
            var result = await _api.GetIntegrationLogsAsync(_integrationId, resource.Id);
            LogsBox.Text = result.TryGetProperty("logs", out var logs) ? logs.GetString() ?? string.Empty : result.ToString();
        }
        catch (Exception exception) when (exception is ControllerApiException or HttpRequestException or TaskCanceledException)
        {
            LogsBox.Text = exception.Message;
        }
    }
}
