using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AppleMusicTranslator.Models;
using AppleMusicTranslator.Services;
using WpfColor = System.Windows.Media.Color;

namespace AppleMusicTranslator;

public partial class IslandLyricsWindow : Window
{
    private readonly MainWindow _controller;
    private readonly AppSettings _settings;
    private bool _suppressPlacementSave;

    public IslandLyricsWindow(MainWindow controller, AppSettings settings)
    {
        InitializeComponent();
        _controller = controller;
        _settings = settings;
        RestorePlacement();
    }

    public void ApplySettings(UiText ui)
    {
        var background = ColorFromHex(_settings.BackgroundColor, WpfColor.FromRgb(24, 27, 34));
        var mainColor = ColorFromHex(_settings.MainColor, Colors.White);
        var originalColor = ColorFromHex(_settings.OriginalColor, WpfColor.FromArgb(0xD5, 0xFF, 0xFF, 0xFF));
        if (_settings.AutoContrastText)
        {
            mainColor = EnsureReadable(mainColor, background, preferStrong: true);
            originalColor = EnsureReadable(originalColor, background, preferStrong: false);
        }

        MainText.FontSize = Math.Clamp(_settings.MainFontSize * 0.66, 17, 28);
        OriginalText.FontSize = Math.Clamp(_settings.OriginalFontSize * 0.72, 11, 16);
        MainText.Foreground = new SolidColorBrush(mainColor);
        OriginalText.Foreground = new SolidColorBrush(originalColor);
        AccentBar.Background = new SolidColorBrush(ColorFromHex(_settings.AccentColor, WpfColor.FromRgb(93, 255, 230)));
        Shell.BorderBrush = new SolidColorBrush(ColorFromHex(WithAlpha(_settings.AccentColor, _settings.BorderOpacity), Colors.Cyan));
        Shell.CornerRadius = new CornerRadius(Math.Max(28, _settings.IslandHeight / 2));

        SettingsMenuItem.Header = ui.Settings;
        RescanMenuItem.Header = ui.RescanLyrics;
        TranslationMenuItem.Header = _settings.ShowTranslation ? ui.ToggleTranslationOn : ui.ToggleTranslationOff;
        ExitIslandMenuItem.Header = ui.NormalWindow;
    }

    public void UpdateDisplay(LyricDisplayPayload payload)
    {
        SetText(payload.Original, payload.Main);
        ApplyIslandSize();
        PulseIsland();
    }

    private void SetText(string original, string main)
    {
        var hasOriginal = !string.IsNullOrWhiteSpace(original);
        OriginalText.Visibility = hasOriginal ? Visibility.Visible : Visibility.Collapsed;
        MainText.Margin = hasOriginal ? new Thickness(0, 2, 0, 0) : new Thickness(0);
        OriginalText.Text = original;
        MainText.Text = main;
    }

    private void RestorePlacement()
    {
        _suppressPlacementSave = true;
        try
        {
            ApplyIslandSize(animate: false);
            var workArea = SystemParameters.WorkArea;
            if (_settings.IslandSnapToTop || !double.IsFinite(_settings.IslandLeft) || !double.IsFinite(_settings.IslandTop))
            {
                Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
                Top = workArea.Top + Math.Clamp(_settings.IslandTopOffset, 0, 120);
            }
            else
            {
                Left = Math.Clamp(_settings.IslandLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
                Top = Math.Clamp(_settings.IslandTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
            }
        }
        finally
        {
            _suppressPlacementSave = false;
        }
    }

    private void ApplyIslandSize(bool animate = true)
    {
        var hasTranslation = _settings.ShowTranslation && !string.IsNullOrWhiteSpace(OriginalText.Text);
        var activeTextLength = Math.Max(MainText.Text?.Length ?? 0, OriginalText.Text?.Length ?? 0);
        var dynamicWidth = _settings.IslandWidth + Math.Clamp(activeTextLength * 3.2, 0, 160);
        var dynamicHeight = _settings.IslandHeight + (hasTranslation ? 14 : 0);
        var targetWidth = Math.Clamp(dynamicWidth, 360, Math.Min(1040, SystemParameters.WorkArea.Width - 48));
        var targetHeight = Math.Clamp(dynamicHeight, 64, 150);

        if (!animate || !IsLoaded)
        {
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            Width = targetWidth;
            Height = targetHeight;
        }
        else
        {
            BeginAnimation(WidthProperty, new DoubleAnimation
            {
                To = targetWidth,
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            BeginAnimation(HeightProperty, new DoubleAnimation
            {
                To = targetHeight,
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        if (_settings.IslandSnapToTop)
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Left + Math.Max(0, (workArea.Width - targetWidth) / 2);
            Top = workArea.Top + Math.Clamp(_settings.IslandTopOffset, 0, 120);
        }
    }

    private void PulseIsland()
    {
        if (!_settings.EnableIslandBreathing)
        {
            return;
        }

        ShellScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            From = 0.985,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        ShellScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation
        {
            From = 0.965,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void RememberPlacement()
    {
        if (_suppressPlacementSave)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        if (Top <= workArea.Top + 80)
        {
            _settings.IslandSnapToTop = true;
            _settings.IslandTopOffset = Math.Clamp(Math.Round(Top - workArea.Top), 0, 120);
            _settings.IslandLeft = double.NaN;
            _settings.IslandTop = double.NaN;
            RestorePlacement();
        }
        else
        {
            _settings.IslandSnapToTop = false;
            _settings.IslandLeft = Left;
            _settings.IslandTop = Top;
        }

        _controller.SaveSettingsOnly();
    }

    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            RememberPlacement();
        }
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        if (IsLoaded)
        {
            RememberPlacement();
        }
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) => _controller.OpenSettingsFromChild();

    private void RescanMenuItem_Click(object sender, RoutedEventArgs e) => _controller.RescanLyricsFromChild();

    private void TranslationMenuItem_Click(object sender, RoutedEventArgs e) => _controller.ToggleTranslationFromChild();

    private void ExitIslandMenuItem_Click(object sender, RoutedEventArgs e) => _controller.ActivateWindowShellMode(WindowShellMode.Normal);

    private static WpfColor ColorFromHex(string value, WpfColor fallback)
    {
        try
        {
            return (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(NormalizeHexColor(value, includeAlpha: true));
        }
        catch
        {
            return fallback;
        }
    }

    private static WpfColor EnsureReadable(WpfColor foreground, WpfColor background, bool preferStrong)
    {
        if (ContrastRatio(foreground, background) >= (preferStrong ? 4.5 : 3.0))
        {
            return foreground;
        }

        var white = Colors.White;
        var black = Colors.Black;
        return ContrastRatio(white, background) >= ContrastRatio(black, background) ? white : black;
    }

    private static double ContrastRatio(WpfColor foreground, WpfColor background)
    {
        var front = RelativeLuminance(foreground);
        var back = RelativeLuminance(background);
        var lighter = Math.Max(front, back);
        var darker = Math.Min(front, back);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(WpfColor color)
    {
        static double Channel(byte value)
        {
            var scaled = value / 255.0;
            return scaled <= 0.03928 ? scaled / 12.92 : Math.Pow((scaled + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static string WithAlpha(string value, double opacity)
    {
        try
        {
            var color = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(NormalizeHexColor(value, includeAlpha: true));
            color.A = (byte)Math.Clamp(opacity * 255, 0, 255);
            return color.ToString();
        }
        catch
        {
            return "#665DFFE6";
        }
    }

    private static string NormalizeHexColor(string value, bool includeAlpha)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "#FFFFFF" : value.Trim();
        if (!normalized.StartsWith('#'))
        {
            normalized = "#" + normalized;
        }

        if (normalized.Length == 7 && includeAlpha)
        {
            normalized = "#FF" + normalized[1..];
        }

        return normalized;
    }
}
