using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AppleMusicTranslator.Models;
using AppleMusicTranslator.Services;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace AppleMusicTranslator;

public partial class LyricsOnlyWindow : Window
{
    private readonly MainWindow _controller;
    private readonly AppSettings _settings;
    private ScrollViewerOffsetAnimator? _verticalScrollAnimator;
    private bool _suppressPlacementSave;

    public LyricsOnlyWindow(MainWindow controller, AppSettings settings)
    {
        InitializeComponent();
        _controller = controller;
        _settings = settings;
        _verticalScrollAnimator = new ScrollViewerOffsetAnimator(VerticalView);
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

        MainText.FontSize = _settings.MainFontSize;
        MainText.LineHeight = Math.Max(_settings.MainFontSize * 1.24, _settings.MainFontSize + 4);
        MainText.Foreground = new SolidColorBrush(mainColor);
        OriginalText.FontSize = _settings.OriginalFontSize;
        OriginalText.Foreground = new SolidColorBrush(originalColor);

        SettingsMenuItem.Header = ui.Settings;
        RescanMenuItem.Header = ui.RescanLyrics;
        TranslationMenuItem.Header = _settings.ShowTranslation ? ui.ToggleTranslationOn : ui.ToggleTranslationOff;
        ExitLyricsOnlyMenuItem.Header = ui.ExitLyricsOnly;
    }

    public void UpdateDisplay(LyricDisplayPayload payload)
    {
        if (payload.LayoutMode == LyricsLayoutMode.Vertical)
        {
            CenterView.Visibility = Visibility.Collapsed;
            VerticalView.Visibility = Visibility.Visible;
            UpdateVertical(payload);
            return;
        }

        VerticalView.Visibility = Visibility.Collapsed;
        CenterView.Visibility = Visibility.Visible;
        SetText(payload.Original, payload.Main);
    }

    private void SetText(string original, string main)
    {
        var hasOriginal = !string.IsNullOrWhiteSpace(original);
        OriginalText.Visibility = hasOriginal ? Visibility.Visible : Visibility.Collapsed;
        MainText.Margin = hasOriginal ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        OriginalText.Text = original;
        MainText.Text = main;
    }

    private void UpdateVertical(LyricDisplayPayload payload)
    {
        VerticalPanel.Children.Clear();
        if (payload.ActiveLine is null)
        {
            return;
        }

        var background = ColorFromHex(_settings.BackgroundColor, WpfColor.FromRgb(24, 27, 34));
        var mainColor = ColorFromHex(_settings.MainColor, Colors.White);
        var originalColor = ColorFromHex(_settings.OriginalColor, WpfColor.FromArgb(0xD5, 0xFF, 0xFF, 0xFF));
        var accentColor = ColorFromHex(_settings.AccentColor, WpfColor.FromRgb(93, 255, 230));
        if (_settings.AutoContrastText)
        {
            mainColor = EnsureReadable(mainColor, background, preferStrong: true);
            originalColor = EnsureReadable(originalColor, background, preferStrong: false);
        }

        var activeIndex = 0;
        for (var index = 0; index < payload.Lines.Count; index++)
        {
            var line = payload.Lines[index];
            if (ReferenceEquals(line, payload.ActiveLine)
                || line.Begin == payload.ActiveLine.Begin && line.Text == payload.ActiveLine.Text)
            {
                activeIndex = index;
                break;
            }
        }

        for (var index = 0; index < payload.Lines.Count; index++)
        {
            var line = payload.Lines[index];
            var isActive = index == activeIndex;
            var translated = payload.ShowTranslation && payload.Translations.TryGetValue(line.Text, out var value)
                ? value
                : string.Empty;
            var primaryText = string.IsNullOrWhiteSpace(translated) ? line.Text : translated;

            var linePanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
            if (!string.IsNullOrWhiteSpace(translated))
            {
                linePanel.Children.Add(CreateLyricTextBlock(
                    line.Text,
                    originalColor,
                    isActive ? _settings.OriginalFontSize : Math.Max(12, _settings.OriginalFontSize - 2),
                    isActive ? 0.82 : 0.38,
                    FontWeights.Normal));
            }

            linePanel.Children.Add(CreateLyricTextBlock(
                primaryText,
                isActive ? mainColor : originalColor,
                isActive ? _settings.MainFontSize : Math.Max(14, _settings.OriginalFontSize + 1),
                isActive ? 1.0 : 0.42,
                isActive ? FontWeights.Bold : FontWeights.SemiBold));

            VerticalPanel.Children.Add(new Border
            {
                Margin = new Thickness(0, isActive ? 8 : 4, 0, isActive ? 8 : 4),
                Padding = new Thickness(isActive ? 14 : 8, isActive ? 10 : 6, isActive ? 14 : 8, isActive ? 10 : 6),
                BorderThickness = isActive ? new Thickness(0, 0, 0, 2) : new Thickness(0),
                BorderBrush = new SolidColorBrush(accentColor),
                Opacity = isActive ? 1 : 0.72,
                Child = linePanel
            });
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!_settings.AutoScrollLongLyrics)
            {
                return;
            }

            VerticalView.UpdateLayout();
            if (activeIndex >= VerticalPanel.Children.Count
                || VerticalPanel.Children[activeIndex] is not FrameworkElement activeElement)
            {
                return;
            }

            var point = activeElement.TranslatePoint(new WpfPoint(0, 0), VerticalPanel);
            var anchor = _settings.AutoCenterCurrentLyric ? 0.5 : 0.35;
            var target = Math.Max(0, point.Y - Math.Max(40, VerticalView.ViewportHeight * anchor));
            AnimateVerticalScroll(target);
        });
    }

    private void RestorePlacement()
    {
        _suppressPlacementSave = true;
        try
        {
            Width = Math.Clamp(_settings.LyricsOnlyWidth, 360, 2200);
            Height = Math.Clamp(_settings.LyricsOnlyHeight, 120, 900);
            if (double.IsFinite(_settings.LyricsOnlyLeft) && double.IsFinite(_settings.LyricsOnlyTop))
            {
                var workArea = SystemParameters.WorkArea;
                Left = Math.Clamp(_settings.LyricsOnlyLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
                Top = Math.Clamp(_settings.LyricsOnlyTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
            }
            else
            {
                var workArea = SystemParameters.WorkArea;
                Left = workArea.Left + (workArea.Width - Width) / 2;
                Top = workArea.Bottom - Height - 80;
            }
        }
        finally
        {
            _suppressPlacementSave = false;
        }
    }

    private void RememberPlacement()
    {
        if (_suppressPlacementSave || WindowState != WindowState.Normal)
        {
            return;
        }

        _settings.LyricsOnlyLeft = Left;
        _settings.LyricsOnlyTop = Top;
        _settings.LyricsOnlyWidth = Math.Clamp(Width, 360, 2200);
        _settings.LyricsOnlyHeight = Math.Clamp(Height, 120, 900);
        _controller.SaveSettingsOnly();
    }

    private void AnimateVerticalScroll(double target)
    {
        _verticalScrollAnimator ??= new ScrollViewerOffsetAnimator(VerticalView);
        var maximum = Math.Max(0, VerticalView.ExtentHeight - VerticalView.ViewportHeight);
        _verticalScrollAnimator.Offset = VerticalView.VerticalOffset;
        _verticalScrollAnimator.BeginAnimation(
            ScrollViewerOffsetAnimator.OffsetProperty,
            new DoubleAnimation
            {
                To = Math.Clamp(target, 0, maximum),
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            RememberPlacement();
        }
    }

    private void Window_LocationChanged(object? sender, EventArgs e) => RememberPlacement();

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => RememberPlacement();

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) => _controller.OpenSettingsFromChild();

    private void RescanMenuItem_Click(object sender, RoutedEventArgs e) => _controller.RescanLyricsFromChild();

    private void TranslationMenuItem_Click(object sender, RoutedEventArgs e) => _controller.ToggleTranslationFromChild();

    private void ExitLyricsOnlyMenuItem_Click(object sender, RoutedEventArgs e) => _controller.ActivateWindowShellMode(WindowShellMode.Normal);

    private static TextBlock CreateLyricTextBlock(string text, WpfColor color, double fontSize, double opacity, FontWeight weight) =>
        new()
        {
            Text = text,
            Foreground = new SolidColorBrush(color),
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = fontSize,
            FontWeight = weight,
            Opacity = opacity,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2)
        };

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

    private sealed class ScrollViewerOffsetAnimator : Animatable
    {
        private readonly ScrollViewer _scrollViewer;

        public ScrollViewerOffsetAnimator(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;
        }

        public static readonly DependencyProperty OffsetProperty = DependencyProperty.Register(
            nameof(Offset),
            typeof(double),
            typeof(ScrollViewerOffsetAnimator),
            new PropertyMetadata(0.0, OnOffsetChanged));

        public double Offset
        {
            get => (double)GetValue(OffsetProperty);
            set => SetValue(OffsetProperty, value);
        }

        private static void OnOffsetChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            if (dependencyObject is ScrollViewerOffsetAnimator animator)
            {
                animator._scrollViewer.ScrollToVerticalOffset((double)e.NewValue);
            }
        }

        protected override Freezable CreateInstanceCore() => new ScrollViewerOffsetAnimator(_scrollViewer);
    }
}
