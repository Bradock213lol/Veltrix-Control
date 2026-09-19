using System.Globalization;
using System.Windows;
using VeltrixControl.Contracts;

namespace VeltrixControl.Desktop;

public partial class GameServerWindow : Window
{
    public GameServerWindow()
    {
        InitializeComponent();
        var folder = "servers\\server-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture).ToLowerInvariant();
        InstallPathBox.Text = folder;
        NameBox.Text = "Minecraft server";
    }

    public GameServerRequest? Result { get; private set; }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(InstallPathBox.Text))
        {
            ErrorText.Text = "A server name and install folder are required.";
            return;
        }
        if (!int.TryParse(PortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
            !int.TryParse(MemoryBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var memory) ||
            !int.TryParse(CpuBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cpu))
        {
            ErrorText.Text = "Port, memory, and CPU must be whole numbers.";
            return;
        }

        Result = new GameServerRequest(
            NameBox.Text.Trim(),
            (AdapterBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "MinecraftJava",
            InstallPathBox.Text.Trim(),
            port,
            memory,
            cpu,
            AutoRestartCheck.IsChecked == true,
            string.IsNullOrWhiteSpace(VersionBox.Text) ? null : VersionBox.Text.Trim(),
            null);
        DialogResult = true;
    }
}
