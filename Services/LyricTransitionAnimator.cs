using System.Windows;
using System.Windows.Media.Animation;

namespace AppleMusicTranslator.Services;

public static class LyricTransitionAnimator
{
    public static void FadeText(UIElement element, Action onSwitch, double durationMs = 180)
    {
        var fadeOut = new DoubleAnimation
        {
            From = element.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        fadeOut.Completed += (_, _) =>
        {
            onSwitch();

            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(durationMs + 40),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            element.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        };

        element.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }
}
