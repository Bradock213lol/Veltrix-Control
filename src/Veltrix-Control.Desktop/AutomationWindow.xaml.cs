using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VeltrixControl.Contracts;

namespace VeltrixControl.Desktop;

public partial class AutomationWindow : Window
{
    public AutomationWindow()
    {
        InitializeComponent();
    }

    public AutomationRequest? Result { get; private set; }

    private void Trigger_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SchedulePanel is null) return;
        var kind = (TriggerBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        SchedulePanel.Visibility = kind == "Schedule" ? Visibility.Visible : Visibility.Collapsed;
        AlertPanel.Visibility = kind == "AlertRaised" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Action_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (OperationPanel is null) return;
        OperationPanel.Visibility = (ActionBox.SelectedItem as ComboBoxItem)?.Content?.ToString() == "QueueOperation"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ErrorText.Text = "A name is required.";
            return;
        }
        if (!int.TryParse(CooldownBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cooldown))
        {
            ErrorText.Text = "The cooldown must be a number of seconds between 30 and 86400.";
            return;
        }

        var triggerKind = (TriggerBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Schedule";
        int? minutes = null;
        if (triggerKind == "Schedule")
        {
            if (!int.TryParse(MinutesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < MonitoringLimits.MinScheduleMinutes)
            {
                ErrorText.Text = $"The schedule interval must be at least {MonitoringLimits.MinScheduleMinutes} minutes.";
                return;
            }
            minutes = parsed;
        }
        var severity = (SeverityBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (severity == "(any)") severity = null;

        var actionKind = (ActionBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CreateAlert";
        var operationKind = actionKind == "QueueOperation"
            ? (OperationBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
            : null;

        Result = new AutomationRequest(
            NameBox.Text.Trim(),
            true,
            new AutomationTriggerRequest(triggerKind,
                string.IsNullOrWhiteSpace(AlertCodeBox.Text) ? null : AlertCodeBox.Text.Trim(),
                severity,
                minutes),
            string.IsNullOrWhiteSpace(DeviceFilterBox.Text) ? null : new AutomationConditionRequest(DeviceFilterBox.Text.Trim(), null),
            new AutomationActionRequest(actionKind, null, null, operationKind),
            cooldown);
        DialogResult = true;
    }
}
