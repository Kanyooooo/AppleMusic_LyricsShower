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
            File.WriteAllText(_settingsPath, json, Encoding.UTF8);
        }
        catch
        {
            // Style persistence should never interrupt playback display.
        }
    }

    private static bool Migrate(AppSettings settings, string rawJson)
    {
        var changed = false;
        var isLegacySettings = !rawJson.Contains(nameof(AppSettings.InterfaceLanguage), StringComparison.Ordinal);
        if (isLegacySettings)
        {
            settings.InterfaceLanguage = UiLanguage.ChineseSimplified;
            changed = true;
        }

        if (!rawJson.Contains(nameof(AppSettings.LyricOffsetMs), StringComparison.Ordinal))
        {
            settings.LyricOffsetMs = new AppSettings().LyricOffsetMs;
            changed = true;
        }
        else if (isLegacySettings && settings.LyricOffsetMs is 500 or 720)
        {
            settings.LyricOffsetMs = new AppSettings().LyricOffsetMs;
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

        if (!rawJson.Contains(nameof(AppSettings.BackgroundColor), StringComparison.Ordinal))
        {
            settings.BackgroundColor = new AppSettings().BackgroundColor;
            changed = true;
        }

        var originalOffset = settings.LyricOffsetMs;
        settings.LyricOffsetMs = Math.Clamp(settings.LyricOffsetMs, 0, 2200);

        var originalX = settings.LyricOffsetX;
        var originalY = settings.LyricOffsetY;
        settings.LyricOffsetX = Math.Clamp(settings.LyricOffsetX, -360, 360);
        settings.LyricOffsetY = Math.Clamp(settings.LyricOffsetY, -180, 180);

        return changed
            || settings.LyricOffsetMs != originalOffset
            || Math.Abs(settings.LyricOffsetX - originalX) > 0.01
            || Math.Abs(settings.LyricOffsetY - originalY) > 0.01;
    }
}
