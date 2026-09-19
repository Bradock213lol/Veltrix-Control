using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VeltrixControl.Desktop;

public sealed class DiffWindow : Window
{
    public DiffWindow(string title, string original, string current)
    {
        Title = $"Compare · {title}";
        Width = 980;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var lines = BuildDiff(original, current);
        var list = new ListBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            ItemsSource = lines.Select(line => new DiffRow(line)).ToList(),
            ItemTemplate = CreateTemplate()
        };

        var legend = new TextBlock
        {
            Text = $"{lines.Count(line => line.Kind == DiffKind.Added)} added · {lines.Count(line => line.Kind == DiffKind.Removed)} removed · compared with current editor content",
            Foreground = (Brush)FindResource("MutedTextBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var panel = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(legend, Dock.Top);
        panel.Children.Add(legend);
        panel.Children.Add(list);
        Content = panel;
    }

    private static DataTemplate CreateTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DiffRow.Display)));
        factory.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(DiffRow.Brush)));
        factory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        return new DataTemplate { VisualTree = factory };
    }

    private static List<DiffLine> BuildDiff(string original, string current)
    {
        var left = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var right = current.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (left.Length > 4000 || right.Length > 4000)
        {
            return [new DiffLine(DiffKind.Context, "Files are too large for an inline diff. Use history restore instead.")];
        }

        var lcs = new int[left.Length + 1, right.Length + 1];
        for (var i = left.Length - 1; i >= 0; i--)
        {
            for (var j = right.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = left[i] == right[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < left.Length && y < right.Length)
        {
            if (left[x] == right[y])
            {
                if (result.Count < 500) result.Add(new DiffLine(DiffKind.Context, "  " + left[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                if (result.Count < 500) result.Add(new DiffLine(DiffKind.Removed, "- " + left[x]));
                x++;
            }
            else
            {
                if (result.Count < 500) result.Add(new DiffLine(DiffKind.Added, "+ " + right[y]));
                y++;
            }
        }
        while (x < left.Length && result.Count < 500) result.Add(new DiffLine(DiffKind.Removed, "- " + left[x++]));
        while (y < right.Length && result.Count < 500) result.Add(new DiffLine(DiffKind.Added, "+ " + right[y++]));
        if (result.All(line => line.Kind == DiffKind.Context)) result.Add(new DiffLine(DiffKind.Context, "No differences."));
        return result;
    }

    private enum DiffKind { Context, Added, Removed }

    private sealed record DiffLine(DiffKind Kind, string Text);

    private sealed class DiffRow(DiffLine line)
    {
        public string Display => line.Text;
        public Brush Brush => line.Kind switch
        {
            DiffKind.Added => (Brush)Application.Current.FindResource("SuccessBrush"),
            DiffKind.Removed => (Brush)Application.Current.FindResource("DangerBrush"),
            _ => (Brush)Application.Current.FindResource("MutedTextBrush")
        };
    }
}
