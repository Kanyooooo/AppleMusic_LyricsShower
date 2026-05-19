using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppleMusicTranslator.Models;
using AppleMusicTranslator.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfColor = System.Windows.Media.Color;

namespace AppleMusicTranslator;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MainWindow _owner;
    private bool _applyingSettings;
    private UiText _ui;

    public SettingsWindow(AppSettings settings, MainWindow owner)
    {
        _settings = settings;
        _owner = owner;
        _ui = UiText.For(_settings.InterfaceLanguage);
        _applyingSettings = true;
        InitializeComponent();
        _applyingSettings = false;
        ReloadFromSettings();
    }

    public void ReloadFromSettings()
    {
        _applyingSettings = true;
        try
        {
            _ui = UiText.For(_settings.InterfaceLanguage);
            ApplyLocalizedText();

            ShowTranslationCheckBox.IsChecked = _settings.ShowTranslation;
            LyricsOnlyCheckBox.IsChecked = _settings.LyricsOnlyMode;
            VerticalLyricsCheckBox.IsChecked = _settings.LayoutMode == LyricsLayoutMode.Vertical;
            IslandModeCheckBox.IsChecked = _settings.LayoutMode == LyricsLayoutMode.Island;
            VerticalLyricsCheckBox.IsEnabled = _settings.LayoutMode != LyricsLayoutMode.Island;
            AutoScrollLongLyricsCheckBox.IsChecked = _settings.AutoScrollLongLyrics;
            AutoCenterCurrentLyricCheckBox.IsChecked = _settings.AutoCenterCurrentLyric;
            AutoLyricsPanelCheckBox.IsChecked = _settings.AutoOpenAppleMusicLyricsPanel;
            AutoContrastCheckBox.IsChecked = _settings.AutoContrastText;
            SelectWindowModeComboItem();
            MainFontSlider.Value = _settings.MainFontSize;
            OriginalFontSlider.Value = _settings.OriginalFontSize;
            BackgroundOpacitySlider.Value = _settings.BackgroundOpacity * 100;
            LyricOffsetSlider.Value = _settings.LyricOffsetMs;
            LyricPositionXSlider.Value = _settings.LyricOffsetX;
            LyricPositionYSlider.Value = _settings.LyricOffsetY;
            IslandWidthSlider.Value = _settings.IslandWidth;
            IslandHeightSlider.Value = _settings.IslandHeight;
            IslandTopOffsetSlider.Value = _settings.IslandTopOffset;
            SelectLanguageComboItem();
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void ApplyLocalizedText()
    {
        Title = _ui.DisplaySettings;
        SettingsPanelTitle.Text = _ui.DisplaySettings;
        CloseSettingsButton.ToolTip = _ui.CloseSettings;
        ShowTranslationCheckBox.Content = _ui.ShowTranslation;
        LyricsOnlyCheckBox.Content = _ui.LyricsOnlyWindow;
        WindowModeLabel.Text = _ui.WindowMode;
        VerticalLyricsCheckBox.Content = _ui.VerticalLyrics;
        IslandModeCheckBox.Content = _ui.DynamicIsland;
        AutoScrollLongLyricsCheckBox.Content = _ui.AutoScrollLongLyrics;
        AutoCenterCurrentLyricCheckBox.Content = _ui.AutoCenterCurrentLyric;
        AutoLyricsPanelCheckBox.Content = _ui.AutoOpenLyricsPanel;
        AutoContrastCheckBox.Content = _ui.AutoContrastText;
        InterfaceLanguageLabel.Text = _ui.InterfaceLanguage;
        LyricOffsetHintText.Text = _ui.LyricsOffsetHint;
        MainFontLabel.Text = _ui.MainTextSize;
        OriginalFontLabel.Text = _ui.OriginalTextSize;
        BackgroundOpacityLabel.Text = _ui.BackgroundOpacity;
        MainColorLabel.Text = _ui.MainColor;
        OriginalColorLabel.Text = _ui.OriginalColor;
        BackgroundColorLabel.Text = _ui.BackgroundColor;
        AccentColorLabel.Text = _ui.AccentColor;
        CustomMainColorButton.Content = _ui.Custom;
        CustomOriginalColorButton.Content = CustomMainColorButton.Content;
        CustomBackgroundColorButton.Content = CustomMainColorButton.Content;
        CustomAccentColorButton.Content = CustomMainColorButton.Content;

        foreach (var item in InterfaceLanguageComboBox.Items.OfType<ComboBoxItem>())
        {
            var tag = item.Tag?.ToString();
            item.Content = tag == nameof(UiLanguage.English) ? _ui.LanguageEnglish : _ui.LanguageChinese;
        }

        foreach (var item in WindowModeComboBox.Items.OfType<ComboBoxItem>())
        {
            item.Content = item.Tag?.ToString() switch
            {
                nameof(WindowShellMode.LyricsOnly) => _ui.LyricsOnlyWindow,
                nameof(WindowShellMode.Island) => _ui.DynamicIsland,
                _ => _ui.NormalWindow
            };
        }

        UpdateDynamicLabels();
    }

    private void UpdateDynamicLabels()
    {
        LyricOffsetLabel.Text = $"{_ui.LyricsOffset}: {_ui.LyricOffsetValue(_settings.LyricOffsetMs)}";
        LyricPositionXLabel.Text = $"{_ui.LyricPositionX}: {_ui.LyricPositionValue(_settings.LyricOffsetX)}";
        LyricPositionYLabel.Text = $"{_ui.LyricPositionY}: {_ui.LyricPositionValue(_settings.LyricOffsetY)}";
        IslandWidthLabel.Text = $"{_ui.IslandWidth}: {_ui.PixelValue(_settings.IslandWidth)}";
        IslandHeightLabel.Text = $"{_ui.IslandHeight}: {_ui.PixelValue(_settings.IslandHeight)}";
        IslandTopOffsetLabel.Text = $"{_ui.IslandTopOffset}: {_ui.PixelValue(_settings.IslandTopOffset)}";
    }

    private void SelectLanguageComboItem()
    {
        var tag = _settings.InterfaceLanguage.ToString();
        foreach (var item in InterfaceLanguageComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                InterfaceLanguageComboBox.SelectedItem = item;
                return;
            }
        }

        InterfaceLanguageComboBox.SelectedIndex = 0;
    }

    private void SelectWindowModeComboItem()
    {
        var tag = _owner.CurrentWindowShellMode.ToString();
        foreach (var item in WindowModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                WindowModeComboBox.SelectedItem = item;
                return;
            }
        }

        WindowModeComboBox.SelectedIndex = 0;
    }

    private void Notify(SettingsChangeKind changeKind)
    {
        if (_applyingSettings)
        {
            return;
        }

        UpdateDynamicLabels();
        _owner.NotifySettingsChanged(changeKind);
    }

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowTranslationCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.ShowTranslation = ShowTranslationCheckBox.IsChecked == true;
        Notify(SettingsChangeKind.Translation);
    }

    private void LyricsOnlyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _owner.SetWindowShellMode(LyricsOnlyCheckBox.IsChecked == true
            ? WindowShellMode.LyricsOnly
            : WindowShellMode.Normal);
        ReloadFromSettings();
        Notify(SettingsChangeKind.Layout);
    }

    private void VerticalLyricsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        if (VerticalLyricsCheckBox.IsChecked == true)
        {
            _settings.LayoutMode = LyricsLayoutMode.Vertical;
        }
        else if (_settings.LayoutMode == LyricsLayoutMode.Vertical)
        {
            _settings.LayoutMode = LyricsLayoutMode.Center;
        }

        ReloadFromSettings();
        Notify(SettingsChangeKind.Layout);
    }

    private void IslandModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _owner.SetWindowShellMode(IslandModeCheckBox.IsChecked == true
            ? WindowShellMode.Island
            : WindowShellMode.Normal);

        ReloadFromSettings();
        Notify(SettingsChangeKind.Layout);
    }

    private void AutoScrollLongLyricsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.AutoScrollLongLyrics = AutoScrollLongLyricsCheckBox.IsChecked == true;
        Notify(SettingsChangeKind.Layout);
    }

    private void AutoCenterCurrentLyricCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.AutoCenterCurrentLyric = AutoCenterCurrentLyricCheckBox.IsChecked == true;
        Notify(SettingsChangeKind.Layout);
    }

    private void AutoLyricsPanelCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.AutoOpenAppleMusicLyricsPanel = AutoLyricsPanelCheckBox.IsChecked == true;
        Notify(SettingsChangeKind.General);
    }

    private void AutoContrastCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.AutoContrastText = AutoContrastCheckBox.IsChecked == true;
        Notify(SettingsChangeKind.General);
    }

    private void InterfaceLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSettings || InterfaceLanguageComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (Enum.TryParse<UiLanguage>(item.Tag?.ToString(), out var language))
        {
            _settings.InterfaceLanguage = language;
            ReloadFromSettings();
            Notify(SettingsChangeKind.Language);
        }
    }

    private void WindowModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSettings || WindowModeComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (Enum.TryParse<WindowShellMode>(item.Tag?.ToString(), out var mode))
        {
            _owner.SetWindowShellMode(mode);
            ReloadFromSettings();
            Notify(SettingsChangeKind.Layout);
        }
    }

    private void LyricOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.LyricOffsetMs = (int)Math.Round(e.NewValue);
        Notify(SettingsChangeKind.Layout);
    }

    private void LyricPositionXSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.LyricOffsetX = Math.Round(e.NewValue);
        Notify(SettingsChangeKind.Layout);
    }

    private void LyricPositionYSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.LyricOffsetY = Math.Round(e.NewValue);
        Notify(SettingsChangeKind.Layout);
    }

    private void MainFontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.MainFontSize = e.NewValue;
        Notify(SettingsChangeKind.Layout);
    }

    private void OriginalFontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.OriginalFontSize = e.NewValue;
        Notify(SettingsChangeKind.Layout);
    }

    private void BackgroundOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.BackgroundOpacity = e.NewValue / 100.0;
        Notify(SettingsChangeKind.General);
    }

    private void IslandWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.IslandWidth = Math.Round(e.NewValue);
        Notify(SettingsChangeKind.Layout);
    }

    private void IslandHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.IslandHeight = Math.Round(e.NewValue);
        Notify(SettingsChangeKind.Layout);
    }

    private void IslandTopOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.IslandTopOffset = Math.Round(e.NewValue);
        _settings.IslandSnapToTop = true;
        Notify(SettingsChangeKind.Layout);
    }

    private void MainColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string color })
        {
            _settings.MainColor = color;
            Notify(SettingsChangeKind.General);
        }
    }

    private void OriginalColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string color })
        {
            _settings.OriginalColor = color;
            Notify(SettingsChangeKind.General);
        }
    }

    private void BackgroundColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string color })
        {
            _settings.BackgroundColor = color;
            Notify(SettingsChangeKind.General);
        }
    }

    private void AccentColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string color })
        {
            _settings.AccentColor = color;
            Notify(SettingsChangeKind.General);
        }
    }

    private void CustomMainColorButton_Click(object sender, RoutedEventArgs e)
    {
        PickColor(_settings.MainColor, color => _settings.MainColor = color);
    }

    private void CustomOriginalColorButton_Click(object sender, RoutedEventArgs e)
    {
        PickColor(_settings.OriginalColor, color => _settings.OriginalColor = color);
    }

    private void CustomBackgroundColorButton_Click(object sender, RoutedEventArgs e)
    {
        PickColor(_settings.BackgroundColor, color => _settings.BackgroundColor = color);
    }

    private void CustomAccentColorButton_Click(object sender, RoutedEventArgs e)
    {
        PickColor(_settings.AccentColor, color => _settings.AccentColor = color);
    }

    private void PickColor(string current, Action<string> apply)
    {
        var color = ColorFromHex(current, Colors.White);
        using var dialog = new Forms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            Color = Drawing.Color.FromArgb(color.R, color.G, color.B)
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        apply($"#FF{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        Notify(SettingsChangeKind.General);
    }

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
