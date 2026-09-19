using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace VeltrixControl.Desktop;

public partial class App : Application
{
    private static bool _usesLightTheme;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            var choice = MessageBox.Show(
                $"Veltrix-Control could not continue.\n\n{eventArgs.Exception.Message}\n\nOpen the browser fallback?",
                "Veltrix-Control",
                MessageBoxButton.YesNo,
                MessageBoxImage.Error);
            if (choice == MessageBoxResult.Yes) OpenBrowserFallback();
            Shutdown(1);
        };
        ApplySystemTheme();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        base.OnStartup(e);
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) ApplyWindowChrome(window);
    }

    private static void ApplyWindowChrome(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var dark = _usesLightTheme ? 0 : 1;
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
            }

            if (TryGetBrushColor("WindowBrush", out var color))
            {
                var colorRef = color.R | (color.G << 8) | (color.B << 16);
                _ = DwmSetWindowAttribute(handle, 35, ref colorRef, sizeof(int));
                _ = DwmSetWindowAttribute(handle, 34, ref colorRef, sizeof(int));
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            // Older Windows builds keep the default title bar.
        }
    }

    private static bool TryGetBrushColor(string key, out Color color)
    {
        color = Colors.Black;
        if (Current?.Resources[key] is SolidColorBrush brush)
        {
            color = brush.Color;
            return true;
        }
        return false;
    }

    private void ApplySystemTheme()
    {
        var themeValue = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme",
            0);
        _usesLightTheme = themeValue is int setting && setting == 1;

        if (!_usesLightTheme) return;
        SetColor("WindowBrush", "#F4F6FB");
        SetColor("SurfaceBrush", "#FFFFFF");
        SetColor("SurfaceMutedBrush", "#EDF0F7");
        SetColor("ElevatedBrush", "#FFFFFF");
        SetColor("HoverBrush", "#E4E9F5");
        SetColor("TextBrush", "#171A29");
        SetColor("MutedTextBrush", "#596178");
        SetColor("BorderBrush", "#D4D9E6");
        SetColor("ControlHoverBorderBrush", "#A9B4CC");
        SetColor("AccentBrush", "#4054D9");
        SetColor("AccentSoftBrush", "#E4E8FF");
        SetColor("CyanBrush", "#007B9E");
        SetColor("SuccessBrush", "#087A55");
        SetColor("WarningBrush", "#8C5A00");
        SetColor("DangerBrush", "#B4233C");
        SetColor("ScrollThumbBrush", "#C3CBDB");
        SetColor("OverlayBrush", "#000000");
    }

    private void SetColor(string key, string value) => Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));

    public static void OpenBrowserFallback(string url = "http://localhost:5187/")
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
