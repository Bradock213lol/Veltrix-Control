using System.Windows;
using System.Windows.Controls;
using VeltrixControl.Contracts;

namespace VeltrixControl.Desktop;

public partial class DeploymentWindow : Window
{
    public DeploymentWindow(SoftwarePackageView package, IReadOnlyList<DeviceRow> devices)
    {
        InitializeComponent();
        PackageText.Text = package.Name;
        PackageDetail.Text = $"{package.Source} · {package.PackageId}" + (package.Version is null ? string.Empty : $" · version {package.Version}");
        DevicesList.ItemsSource = devices;
    }

    public SoftwareAction SelectedAction { get; private set; } = SoftwareAction.Install;
    public Guid[] SelectedDeviceIds { get; private set; } = [];

    private void Deploy_Click(object sender, RoutedEventArgs e)
    {
        var selected = DevicesList.SelectedItems.OfType<DeviceRow>().Select(row => row.Id).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Select at least one device.", "Deployment", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SelectedAction = (ActionBox.SelectedItem as ComboBoxItem)?.Content?.ToString() switch
        {
            "Uninstall" => SoftwareAction.Uninstall,
            "Upgrade" => SoftwareAction.Upgrade,
            _ => SoftwareAction.Install
        };
        SelectedDeviceIds = selected;
        DialogResult = true;
    }
}
