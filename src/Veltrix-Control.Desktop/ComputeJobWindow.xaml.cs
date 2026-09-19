using System.Globalization;
using System.IO;
using System.Windows;
using VeltrixControl.Contracts;

namespace VeltrixControl.Desktop;

public partial class ComputeJobWindow : Window
{
    private readonly Guid? _deviceId;

    public ComputeJobWindow(Guid? deviceId, string deviceName)
    {
        InitializeComponent();
        _deviceId = deviceId;
        Subtitle.Text = deviceId is null
            ? "The scheduler places the job on any eligible online node."
            : $"The scheduler will target {deviceName} when it has capacity and is not in Gaming or Maintenance mode.";
        ExecutableBox.Text = Path.Combine(Environment.SystemDirectory, "ping.exe");
        ArgumentsBox.Text = "-n 1 127.0.0.1";
    }

    public ComputeJobRequest? Result { get; private set; }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(ExecutableBox.Text))
        {
            ErrorText.Text = "A name and an executable path are required.";
            return;
        }
        if (!int.TryParse(PriorityBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority) ||
            !int.TryParse(CpuBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cpu) ||
            !long.TryParse(MemoryBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var memoryMb) ||
            !long.TryParse(DiskBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var diskMb) ||
            !int.TryParse(TimeoutBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout) ||
            !int.TryParse(AttemptsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempts))
        {
            ErrorText.Text = "Numeric fields must contain valid numbers.";
            return;
        }

        Result = new ComputeJobRequest(
            NameBox.Text.Trim(),
            priority,
            new ComputeRequirements(cpu, memoryMb * 1024 * 1024, diskMb * 1024 * 1024, false, null, _deviceId),
            new ComputeCommand(ExecutableBox.Text.Trim(),
                string.IsNullOrWhiteSpace(ArgumentsBox.Text) ? null : ArgumentsBox.Text.Trim(),
                string.IsNullOrWhiteSpace(WorkingDirectoryBox.Text) ? null : WorkingDirectoryBox.Text.Trim()),
            timeout,
            attempts);
        DialogResult = true;
    }
}
