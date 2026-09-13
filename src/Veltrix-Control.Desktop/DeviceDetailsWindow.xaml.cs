using System.Windows;
using System.Globalization;
using System.Net.Http;
using VeltrixControl.Contracts;

namespace VeltrixControl.Desktop;

public partial class DeviceDetailsWindow : Window
{
    private readonly ControllerApiClient _api;
    private readonly DeviceSummary _device;

    public DeviceDetailsWindow(ControllerApiClient api, DeviceSummary device, bool canUsePower)
    {
        InitializeComponent();
        _api = api;
        _device = device;
        DeviceNameText.Text = device.Name;
        DeviceIdentityText.Text = device.Id.ToString("D");
        StatusText.Text = device.Online ? "Online" : "Offline";
        HealthText.Text = $"{device.HealthScore}/100";
        CpuText.Text = device.Telemetry is null ? "—" : $"{device.Telemetry.CpuPercent:0}%";
        MemoryText.Text = device.Telemetry is null ? "—" : DeviceRow.FormatBytes(device.Telemetry.UsedMemoryBytes);
        UptimeText.Text = device.Telemetry is null ? "—" : FormatUptime(device.Telemetry.UptimeSeconds);
        OsText.Text = $"{device.Inventory.OperatingSystem} · {device.Inventory.OsVersion}";
        HardwareText.Text = $"{device.Inventory.Architecture} · {device.Inventory.LogicalProcessors} logical CPUs";
        AgentText.Text = device.Inventory.IsSimulation ? "Simulator" : $"Veltrix-Control Agent {device.Inventory.AgentVersion}";
        HeartbeatText.Text = device.LastHeartbeat.LocalDateTime.ToString("F", CultureInfo.CurrentCulture);
        DiskGrid.ItemsSource = device.Inventory.Disks.Select(disk => new
        {
            disk.Name,
            disk.Format,
            Free = DeviceRow.FormatBytes(disk.AvailableBytes),
            Total = DeviceRow.FormatBytes(disk.TotalBytes)
        });
        RestartButton.IsEnabled = canUsePower && device.Online && !device.Inventory.IsSimulation;
        ShutdownButton.IsEnabled = RestartButton.IsEnabled;
        if (!canUsePower) ActionStatusText.Text = "Your role does not include power actions.";
        else if (!device.Online) ActionStatusText.Text = "Power actions require an online device.";
        else if (device.Inventory.IsSimulation) ActionStatusText.Text = "Power actions are disabled for simulator devices.";
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_device.Id.ToString("D"));
        ActionStatusText.Text = "Device ID copied.";
    }

    private async void Restart_Click(object sender, RoutedEventArgs e) => await QueuePowerAsync(OperationKind.Restart);
    private async void Shutdown_Click(object sender, RoutedEventArgs e) => await QueuePowerAsync(OperationKind.Shutdown);

    private async Task QueuePowerAsync(OperationKind kind)
    {
        var action = kind == OperationKind.Restart ? "restart" : "shut down";
        var result = MessageBox.Show(this,
            $"This will {action} {_device.Name}. The managed node must also allow power actions in its local policy. Continue?",
            $"Confirm {action}", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        RestartButton.IsEnabled = ShutdownButton.IsEnabled = false;
        try
        {
            ActionStatusText.Text = $"Queuing {action}…";
            var operation = await _api.CreateOperationAsync(_device.Id, kind, null, true);
            ActionStatusText.Text = $"{char.ToUpperInvariant(action[0])}{action[1..]} queued · {operation.Id:D}";
        }
        catch (Exception exception) when (exception is ControllerApiException or HttpRequestException or TaskCanceledException)
        {
            ActionStatusText.Text = exception.Message;
            RestartButton.IsEnabled = ShutdownButton.IsEnabled = true;
        }
    }

    private static string FormatUptime(long seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalDays >= 1 ? $"{(int)duration.TotalDays}d {duration.Hours}h" : $"{duration.Hours}h {duration.Minutes}m";
    }
}
