using System.Windows;
using System.Windows.Controls;
using VeltrixControl.Contracts;

namespace VeltrixControl.Desktop;

public partial class IntegrationWindow : Window
{
    public IntegrationWindow()
    {
        InitializeComponent();
        EndpointBox.Text = "npipe://./pipe/docker_engine";
    }

    public IntegrationRequest? Result { get; private set; }

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EndpointLabel is null) return;
        var kind = (KindBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (kind == "Pterodactyl")
        {
            EndpointLabel.Content = "Panel URL (https://panel.example.com)";
            if (EndpointBox.Text.StartsWith("npipe", StringComparison.OrdinalIgnoreCase)) EndpointBox.Text = "https://";
        }
        else
        {
            EndpointLabel.Content = "Docker endpoint (npipe:// or https://)";
            if (EndpointBox.Text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) EndpointBox.Text = "npipe://./pipe/docker_engine";
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(EndpointBox.Text))
        {
            ErrorText.Text = "A name and endpoint are required.";
            return;
        }
        Result = new IntegrationRequest(
            (KindBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Docker",
            NameBox.Text.Trim(),
            EndpointBox.Text.Trim(),
            string.IsNullOrEmpty(CredentialBox.Password) ? null : CredentialBox.Password,
            true);
        DialogResult = true;
    }
}
