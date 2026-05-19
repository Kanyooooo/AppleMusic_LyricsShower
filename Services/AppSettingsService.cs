using System.IO;
using System.Text;
using System.Text.Json;
using AppleMusicTranslator.Models;

namespace AppleMusicTranslator.Services;

public sealed class AppSettingsService
{
    private readonly string _settingsPath;

    public AppSettingsService()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AppleMusicTranslator");
        Directory.CreateDirectory(appData);
        _settingsPath = Path.Combine(appData, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            if (Migrate(settings, json))
            {
                Save(settings);
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            WriteAllTextAtomic(_settingsPath, json);
        }
        catch
        {
            // Style persistence should never interrupt playback display.
        }
    }

    private static bool Migrate(AppSettings settings, string rawJson)
    {
        var changed = false;
        var defaults = new AppSettings();
        var isLegacySettings = !rawJson.Contains(nameof(AppSettings.InterfaceLanguage), StringComparison.Ordinal);
        if (isLegacySettings)
        {
            settings.InterfaceLanguage = UiLanguage.ChineseSimplified;
            changed = true;
        }

        if (!rawJson.Contains(nameof(AppSettings.LyricOffsetMs), StringComparison.Ordinal))
        {
            settings.LyricOffsetMs = defaults.LyricOffsetMs;
            changed = true;
        }
        else if (isLegacySettings && settings.LyricOffsetMs is 500 or 720)
        {
            settings.LyricOffsetMs = defaults.LyricOffsetMs;
            changed = true;
        }

        if (!rawJson.Contains(nameof(AppSettings.AutoOpenAppleMusicLyricsPanel), StringComparison.Ordinal))
        {
            settings.AutoOpenAppleMusicLyricsPanel = true;
            changed = true;
        }

        if (!rawJson.Contains(nameof(AppSettings.AutoContrastText), StringComparison.Ordinal))
        {
            settings.AutoContrastText = true;
            changed = true;
        }

        if (!rawJson.Contains(nameof(AppSettings.AutoScrollLongLyrics), StringComparison.Ordinal))
        {
            settings.AutoScrollLongLyrics = true;
            changed = true;
        }

        if (!rawJson.Contains(nameof(AppSettings.AutoCenterCurrentLyric), StringComparison.Ordinal))
        {
            settings.AutoCenterCurrentLyric = true;
            changed = true;
        }

        if (!rawJson.Contains(nameof(AppSettings.WindowWidth), StringComparison.Ordinal))
        {
            settings.WindowWidth = defaults.WindowWidth;
            settings.WindowHeight = defaults.WindowHeight;
            settings.LyricsOnlyWidth = defaults.LyricsOnlyWidth;
            settings.LyricsOnlyHeight = defaults.LyricsOnlyHeight;
            changed = true;
        }

        if (!rawJson.Contains(nameof(AppSettings.LockLyricsPosition), StringComparison.Ordinal))
        {
            settings.LockLyricsPosition = true;
            changed = true;
        }

        if (!rawJson.Contains(nameof(AppSettings.IslandWidth), StringComparison.Ordinal))
        {
            settings.IslandWidth = defaults.IslandWidth;
            settings.IslandHeight = defaults.IslandHeight;
            settings.IslandTopOffset = defaults.IslandTopOffset;
            settings.IslandSnapToTop = true;
            settings.IslandLeft = double.NaN;
            settings.IslandTop = double.NaN;
            changed = true;
        }

        if (!rawJson.Contains(nameof(AppSettings.BackgroundColor), StringComparison.Ordinal))
        {
            settings.BackgroundColor = defaults.BackgroundColor;
            changed = true;
        }

        var originalLanguage = settings.InterfaceLanguage;
        var originalLayoutMode = settings.LayoutMode;
        var originalOriginalFontSize = settings.OriginalFontSize;
        var originalMainFontSize = settings.MainFontSize;
        var originalBackgroundOpacity = settings.BackgroundOpacity;
        var originalBorderOpacity = settings.BorderOpacity;
        var originalOffset = settings.LyricOffsetMs;
        var originalWindowLeft = settings.WindowLeft;
        var originalWindowTop = settings.WindowTop;
        var originalLyricsOnlyLeft = settings.LyricsOnlyLeft;
        var originalLyricsOnlyTop = settings.LyricsOnlyTop;
        var originalIslandLeft = settings.IslandLeft;
        var originalIslandTop = settings.IslandTop;
        var originalOriginalColor = settings.OriginalColor;
        var originalMainColor = settings.MainColor;
        var originalBackgroundColor = settings.BackgroundColor;
        var originalAccentColor = settings.AccentColor;

        if (!Enum.IsDefined(settings.InterfaceLanguage))
        {
            settings.InterfaceLanguage = defaults.InterfaceLanguage;
        }

        if (!Enum.IsDefined(settings.LayoutMode))
        {
            settings.LayoutMode = defaults.LayoutMode;
        }

        settings.OriginalFontSize = ClampFinite(settings.OriginalFontSize, 12, 32, defaults.OriginalFontSize);
        settings.MainFontSize = ClampFinite(settings.MainFontSize, 20, 56, defaults.MainFontSize);
        settings.BackgroundOpacity = ClampFinite(settings.BackgroundOpacity, 0, 1, defaults.BackgroundOpacity);
        settings.BorderOpacity = ClampFinite(settings.BorderOpacity, 0, 1, defaults.BorderOpacity);
        settings.LyricOffsetMs = Math.Clamp(settings.LyricOffsetMs, 0, 2200);

        var originalX = settings.LyricOffsetX;
        var originalY = settings.LyricOffsetY;
        settings.LyricOffsetX = ClampFinite(settings.LyricOffsetX, -360, 360, defaults.LyricOffsetX);
        settings.LyricOffsetY = ClampFinite(settings.LyricOffsetY, -180, 180, defaults.LyricOffsetY);

        var originalWindowWidth = settings.WindowWidth;
        var originalWindowHeight = settings.WindowHeight;
        var originalLyricsOnlyWidth = settings.LyricsOnlyWidth;
        var originalLyricsOnlyHeight = settings.LyricsOnlyHeight;
        var originalIslandWidth = settings.IslandWidth;
        var originalIslandHeight = settings.IslandHeight;
        var originalIslandTopOffset = settings.IslandTopOffset;
        settings.WindowWidth = ClampFinite(settings.WindowWidth, 520, 2200, defaults.WindowWidth);
        settings.WindowHeight = ClampFinite(settings.WindowHeight, 180, 1400, defaults.WindowHeight);
        settings.LyricsOnlyWidth = ClampFinite(settings.LyricsOnlyWidth, 360, 2200, defaults.LyricsOnlyWidth);
        settings.LyricsOnlyHeight = ClampFinite(settings.LyricsOnlyHeight, 120, 900, defaults.LyricsOnlyHeight);
        settings.IslandWidth = ClampFinite(settings.IslandWidth, 360, 1200, defaults.IslandWidth);
        settings.IslandHeight = ClampFinite(settings.IslandHeight, 68, 180, defaults.IslandHeight);
        settings.IslandTopOffset = ClampFinite(settings.IslandTopOffset, 0, 120, defaults.IslandTopOffset);

        settings.WindowLeft = FiniteOrNaN(settings.WindowLeft);
        settings.WindowTop = FiniteOrNaN(settings.WindowTop);
        settings.LyricsOnlyLeft = FiniteOrNaN(settings.LyricsOnlyLeft);
        settings.LyricsOnlyTop = FiniteOrNaN(settings.LyricsOnlyTop);
        settings.IslandLeft = FiniteOrNaN(settings.IslandLeft);
        settings.IslandTop = FiniteOrNaN(settings.IslandTop);

        settings.OriginalColor = ValidHexColorOrDefault(settings.OriginalColor, defaults.OriginalColor);
        settings.MainColor = ValidHexColorOrDefault(settings.MainColor, defaults.MainColor);
        settings.BackgroundColor = ValidHexColorOrDefault(settings.BackgroundColor, defaults.BackgroundColor);
        settings.AccentColor = ValidHexColorOrDefault(settings.AccentColor, defaults.AccentColor);

        return changed
            || settings.InterfaceLanguage != originalLanguage
            || settings.LayoutMode != originalLayoutMode
            || HasChanged(settings.OriginalFontSize, originalOriginalFontSize)
            || HasChanged(settings.MainFontSize, originalMainFontSize)
            || HasChanged(settings.BackgroundOpacity, originalBackgroundOpacity)
            || HasChanged(settings.BorderOpacity, originalBorderOpacity)
            || settings.LyricOffsetMs != originalOffset
            || HasChanged(settings.LyricOffsetX, originalX)
            || HasChanged(settings.LyricOffsetY, originalY)
            || HasChanged(settings.WindowWidth, originalWindowWidth)
            || HasChanged(settings.WindowHeight, originalWindowHeight)
            || HasChanged(settings.LyricsOnlyWidth, originalLyricsOnlyWidth)
            || HasChanged(settings.LyricsOnlyHeight, originalLyricsOnlyHeight)
            || HasChanged(settings.IslandWidth, originalIslandWidth)
            || HasChanged(settings.IslandHeight, originalIslandHeight)
            || HasChanged(settings.IslandTopOffset, originalIslandTopOffset)
            || HasChanged(settings.WindowLeft, originalWindowLeft)
            || HasChanged(settings.WindowTop, originalWindowTop)
            || HasChanged(settings.LyricsOnlyLeft, originalLyricsOnlyLeft)
            || HasChanged(settings.LyricsOnlyTop, originalLyricsOnlyTop)
            || HasChanged(settings.IslandLeft, originalIslandLeft)
            || HasChanged(settings.IslandTop, originalIslandTop)
            || !string.Equals(settings.OriginalColor, originalOriginalColor, StringComparison.Ordinal)
            || !string.Equals(settings.MainColor, originalMainColor, StringComparison.Ordinal)
            || !string.Equals(settings.BackgroundColor, originalBackgroundColor, StringComparison.Ordinal)
            || !string.Equals(settings.AccentColor, originalAccentColor, StringComparison.Ordinal);
    }

    private static double ClampFinite(double value, double min, double max, double fallback) =>
        IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static double FiniteOrNaN(double value) =>
        IsFinite(value) ? value : double.NaN;

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool HasChanged(double left, double right)
    {
        if (!IsFinite(left) || !IsFinite(right))
        {
            return double.IsNaN(left) != double.IsNaN(right)
                || double.IsInfinity(left) != double.IsInfinity(right);
        }

        return Math.Abs(left - right) > 0.01;
    }

    private static string ValidHexColorOrDefault(string value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        if (normalized.StartsWith('#'))
        {
            normalized = normalized[1..];
        }

        return normalized.Length is 6 or 8 && normalized.All(Uri.IsHexDigit)
            ? value.Trim()
            : fallback;
    }

    private static void WriteAllTextAtomic(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, text, Encoding.UTF8);
        File.Move(tempPath, path, overwrite: true);
    }
}
