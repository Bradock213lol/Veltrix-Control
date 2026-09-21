using System.Windows;
using System.Windows.Controls;
using VeltrixControl.Contracts;

namespace VeltrixControl.Desktop;

public partial class SoftwarePackageWindow : Window
{
    public SoftwarePackageWindow()
    {
        InitializeComponent();
    }

    public SoftwarePackageRequest? Result { get; private set; }

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ShaBox is null || PackageIdLabel is null) return;
        var sourceName = (SourceBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var isWinget = sourceName is "Winget" or "Chocolatey";
        ShaBox.IsEnabled = !isWinget;
        PackageIdLabel.Content = isWinget ? "WinGet package identifier" : "HTTPS download URL";
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ErrorText.Text = "A display name is required.";
            return;
        }
        if (string.IsNullOrWhiteSpace(PackageIdBox.Text))
        {
            ErrorText.Text = "A package identifier or URL is required.";
            return;
        }
        var sourceName = (SourceBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var source = sourceName switch
        {
            "Msi" => SoftwareSource.Msi,
            "Exe" => SoftwareSource.Exe,
            "Chocolatey" => SoftwareSource.Choco,
            _ => SoftwareSource.Winget
        };
        if (source is not (SoftwareSource.Winget or SoftwareSource.Choco))
        {
            if (!Uri.TryCreate(PackageIdBox.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                ErrorText.Text = "MSI and EXE packages require an HTTPS download URL.";
                return;
            }
            if (ShaBox.Text.Trim().Length != 64)
            {
                ErrorText.Text = "A 64-character SHA-256 checksum is required.";
                return;
            }
        }
        Result = new SoftwarePackageRequest(
            NameBox.Text.Trim(),
            source,
            PackageIdBox.Text.Trim(),
            string.IsNullOrWhiteSpace(VersionBox.Text) ? null : VersionBox.Text.Trim(),
            string.IsNullOrWhiteSpace(ShaBox.Text) ? null : ShaBox.Text.Trim(),
            string.IsNullOrWhiteSpace(SilentBox.Text) ? null : SilentBox.Text.Trim());
        DialogResult = true;
    }
}
