namespace AppleMusicTranslator.Models;

public sealed class AppSettings
{
    public bool ShowTranslation { get; set; } = true;

    public bool LyricsOnlyMode { get; set; }

    public bool AutoOpenAppleMusicLyricsPanel { get; set; } = true;

    public bool AutoContrastText { get; set; } = true;

    public bool AutoScrollLongLyrics { get; set; } = true;

    public bool AutoCenterCurrentLyric { get; set; } = true;

    public LyricsLayoutMode LayoutMode { get; set; } = LyricsLayoutMode.Center;

    public UiLanguage InterfaceLanguage { get; set; } = UiLanguage.ChineseSimplified;

    public double OriginalFontSize { get; set; } = 18;

    public double MainFontSize { get; set; } = 30;

    public double BackgroundOpacity { get; set; } = 0.86;

    public double BorderOpacity { get; set; } = 0.45;

    public int LyricOffsetMs { get; set; } = 950;

    public double LyricOffsetX { get; set; }

    public double LyricOffsetY { get; set; }

    public double WindowLeft { get; set; } = double.NaN;

    public double WindowTop { get; set; } = double.NaN;

    public double WindowWidth { get; set; } = 980;

    public double WindowHeight { get; set; } = 360;

    public double LyricsOnlyWidth { get; set; } = 860;

    public double LyricsOnlyHeight { get; set; } = 220;

    public double LyricsOnlyLeft { get; set; } = double.NaN;

    public double LyricsOnlyTop { get; set; } = double.NaN;

    public double IslandWidth { get; set; } = 620;

    public double IslandHeight { get; set; } = 86;

    public double IslandTopOffset { get; set; } = 10;

    public bool IslandSnapToTop { get; set; } = true;

    public double IslandLeft { get; set; } = double.NaN;

    public double IslandTop { get; set; } = double.NaN;

    public bool LockLyricsPosition { get; set; } = true;

    public bool EnableIslandBreathing { get; set; } = true;

    public string OriginalColor { get; set; } = "#D5FFFFFF";

    public string MainColor { get; set; } = "#FFFFFFFF";

    public string BackgroundColor { get; set; } = "#181B22";

    public string AccentColor { get; set; } = "#5DFFE6";
}
