using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VeltrixControl.Contracts;

namespace VeltrixControl.Desktop;

public partial class GameServerConsoleWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ControllerApiClient _api;
    private readonly Guid _serverId;
    private readonly DispatcherTimer _timer = new();
    private long _sequence;
    private bool _polling;

    public GameServerConsoleWindow(ControllerApiClient api, GameServerView server)
    {
        InitializeComponent();
        _api = api;
        _serverId = server.Id;
        Title = $"Console · {server.Name}";
        StatusText.Text = $"{server.Name} · {server.State} · port {server.Port}";
        _timer.Interval = TimeSpan.FromSeconds(1.5);
        _timer.Tick += async (_, _) => await PollAsync();
        Loaded += async (_, _) => { _timer.Start(); await PollAsync(); };
        Closed += (_, _) => _timer.Stop();
        InputBox.Focus();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await PollAsync();

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => _ = SendAsync();

    private async Task SendAsync()
    {
        var command = InputBox.Text;
        if (string.IsNullOrWhiteSpace(command)) return;
        InputBox.Clear();
        try
        {
            var operation = await _api.GameServerConsoleAsync(_serverId, command);
            var completed = await _api.WaitForOperationAsync(operation.Id, TimeSpan.FromSeconds(30));
            if (completed.State != OperationState.Succeeded) throw new InvalidOperationException(completed.Error ?? $"The command ended with {completed.State}.");
            Append(completed.ResultJson);
        }
        catch (Exception exception) when (exception is ControllerApiException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var operation = await _api.GameServerOutputAsync(_serverId, _sequence);
            var completed = await _api.WaitForOperationAsync(operation.Id, TimeSpan.FromSeconds(25));
            if (completed.State != OperationState.Succeeded) return;
            Append(completed.ResultJson);
        }
        catch (Exception exception) when (exception is ControllerApiException or HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _polling = false;
        }
    }

    private void Append(string? resultJson)
    {
        var status = JsonSerializer.Deserialize<GameServerStatusResult>(resultJson ?? "{}", JsonOptions);
        if (status is null) return;
        if (status.Output.Length > 0)
        {
            OutputBox.AppendText(status.Output);
            OutputBox.ScrollToEnd();
        }
        if (status.Sequence > _sequence) _sequence = status.Sequence;
        if (status.Exited)
        {
            StatusText.Text = $"{Title} · process exited with code {status.ExitCode}";
            _timer.Stop();
        }
    }
}
