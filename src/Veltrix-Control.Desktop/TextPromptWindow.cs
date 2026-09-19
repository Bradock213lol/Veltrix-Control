using System.Windows;
using System.Windows.Controls;

namespace VeltrixControl.Desktop;

public sealed class TextPromptWindow : Window
{
    private readonly TextBox _value;

    private TextPromptWindow(string title, string prompt, string initial, bool multiline)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        _value = new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 18), AcceptsReturn = multiline, MinHeight = multiline ? 90 : 0 };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Style = TryFindResource("PrimaryButton") as Style };
        ok.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 10, 0) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(_value);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _value.Focus(); _value.SelectAll(); };
    }

    public static string? Show(Window owner, string title, string prompt, string initial = "", bool multiline = false)
    {
        var window = new TextPromptWindow(title, prompt, initial, multiline) { Owner = owner };
        return window.ShowDialog() == true ? window._value.Text : null;
    }
}
