using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VeltrixControl.Desktop;

public partial class App : Application
{
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
            // The Carbon interface is a graphite console in both Windows appearances.
            var dark = 1;
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

    private static void ApplySystemTheme()
    {
        // The delivered Carbon interface is a single graphite theme; the palette in
        // App.xaml is authoritative and is not swapped with the Windows appearance.
    }

    public static void OpenBrowserFallback(string url = "http://localhost:5187/")
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
