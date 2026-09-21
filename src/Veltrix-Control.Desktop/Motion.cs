using System.Windows;
using System.Windows.Media.Animation;

namespace VeltrixControl.Desktop;

public static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static void FadeIn(UIElement element)
    {
        element.Opacity = 1;
        if (!Enabled) return;
        var transform = new System.Windows.Media.TranslateTransform(0, 5);
        element.RenderTransform = transform;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
        transform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    public static void SetBusy(UIElement element, bool busy)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (!busy)
        {
            element.Opacity = 0;
            return;
        }
        if (!Enabled)
        {
            element.Opacity = 1;
            return;
        }
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(650))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
    }
}
