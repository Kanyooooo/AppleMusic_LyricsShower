namespace AppleMusicTranslator.Models;

public sealed class AppSettings
{
    public bool ShowTranslation { get; set; } = true;

    public bool LyricsOnlyMode { get; set; }

    public UiLanguage InterfaceLanguage { get; set; } = UiLanguage.ChineseSimplified;

    public double OriginalFontSize { get; set; } = 18;

    public double MainFontSize { get; set; } = 30;

    public double BackgroundOpacity { get; set; } = 0.86;

    public double BorderOpacity { get; set; } = 0.45;

    public int LyricOffsetMs { get; set; } = 720;

    public string OriginalColor { get; set; } = "#D5FFFFFF";

    public string MainColor { get; set; } = "#FFFFFFFF";

    public string AccentColor { get; set; } = "#5DFFE6";
}
