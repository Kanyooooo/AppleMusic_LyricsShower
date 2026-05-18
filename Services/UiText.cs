using AppleMusicTranslator.Models;

namespace AppleMusicTranslator.Services;

public sealed class UiText
{
    private UiText(UiLanguage language)
    {
        Language = language;
    }

    public UiLanguage Language { get; }

    public static UiText For(UiLanguage language) =>
        language == UiLanguage.English ? English : Chinese;

    public static UiText Chinese { get; } = new(UiLanguage.ChineseSimplified);

    public static UiText English { get; } = new(UiLanguage.English);

    public string WindowTitle => Language == UiLanguage.English ? "Apple Music Translator" : "Apple Music 歌词翻译";
    public string WaitingForAppleMusicTitle => Language == UiLanguage.English ? "Waiting for Apple Music" : "等待 Apple Music";
    public string WaitingForAppleMusicStatus => Language == UiLanguage.English ? "Play a song in Apple Music" : "请在 Apple Music 中播放一首歌";
    public string LoadingLyrics => Language == UiLanguage.English ? "Loading lyrics..." : "正在加载歌词...";
    public string ScanningMemory => Language == UiLanguage.English ? "Scanning Apple Music memory" : "正在扫描 Apple Music 内存";
    public string AppleMusicProcessNotFound => Language == UiLanguage.English ? "Apple Music process not found" : "未找到 Apple Music 进程";
    public string MemoryLyricsMissedFallback => Language == UiLanguage.English ? "Memory lyrics missed; trying LRCLIB fallback" : "内存歌词没有命中，正在尝试 LRCLIB 备用源";
    public string OpenLyricsPanelThenRescan => Language == UiLanguage.English ? "Open Apple Music lyrics once, then rescan" : "请先在 Apple Music 打开一次歌词面板，再重新扫描";
    public string NoLyricsFound => Language == UiLanguage.English ? "No lyrics found" : "没有找到歌词";
    public string LyricLoadingFailed => Language == UiLanguage.English ? "Lyric loading failed" : "歌词加载失败";
    public string RejectedStaleMemoryLyrics => Language == UiLanguage.English ? "Rejected stale memory lyrics" : "已拒绝过期的内存歌词";
    public string AppleMusicMemorySource => Language == UiLanguage.English ? "Apple Music memory lyrics" : "Apple Music 内存歌词";
    public string LrcLibFallbackSource => Language == UiLanguage.English ? "LRCLIB fallback" : "LRCLIB 备用";
    public string LrcLibSyncedFallbackSource => Language == UiLanguage.English ? "LRCLIB synced fallback" : "LRCLIB 同步备用";
    public string LoadedLyricLines(int count) => Language == UiLanguage.English ? $"Loaded {count} lyric lines" : $"已加载 {count} 行歌词";
    public string LoadedCachedLyrics(int count) => Language == UiLanguage.English ? $"Loaded {count} cached lyric lines; verifying live memory" : $"已秒开 {count} 行缓存歌词，正在后台校验";
    public string CachedLyricsLiveMissed => Language == UiLanguage.English ? "Using cached lyrics; live scan did not find a safer replacement" : "正在使用缓存歌词；实时扫描暂未找到更可靠结果";
    public string DeletedCachedLyrics => Language == UiLanguage.English ? "Deleted cached lyrics; rescanning" : "已删除这首歌的歌词缓存，正在重新扫描";
    public string NoCachedLyricsToDelete => Language == UiLanguage.English ? "No cached lyrics for this song; rescanning" : "这首歌还没有歌词缓存，正在重新扫描";
    public string WrongLyricsTooltip => Language == UiLanguage.English ? "Not this song: delete cached lyrics and rescan" : "不是这首歌：删除缓存并重新扫描";
    public string LyricsConflictTooltip(string conflict) => Language == UiLanguage.English
        ? $"Possible lyric cache conflict with {conflict}. Click if this is not this song."
        : $"歌词缓存可能和 {conflict} 冲突。若不是这首歌，请点击删除缓存。";
    public string ReadyTranslatedLines(int count) => Language == UiLanguage.English ? $"Ready: {count} translated lines" : $"翻译就绪：{count} 行";
    public string TranslatingInBackground(int done, int total) => Language == UiLanguage.English ? $"Translating in background {done}/{total}" : $"后台翻译中 {done}/{total}";
    public string ReadVisibleAnchors(int count) => Language == UiLanguage.English ? $"Read {count} visible lyric anchors" : $"已读取 {count} 个可见歌词锚点";
    public string ScanningMemoryStep(int attempt, int total) => Language == UiLanguage.English ? $"Scanning Apple Music memory {attempt}/{total}" : $"正在扫描 Apple Music 内存 {attempt}/{total}";
    public string ShowWindow => Language == UiLanguage.English ? "Show window" : "显示窗口";
    public string Settings => Language == UiLanguage.English ? "Settings" : "设置";
    public string ToggleTranslationOn => Language == UiLanguage.English ? "Hide translation" : "隐藏翻译";
    public string ToggleTranslationOff => Language == UiLanguage.English ? "Show translation" : "显示翻译";
    public string LyricsOnlyMode => Language == UiLanguage.English ? "Lyrics-only mode" : "仅歌词模式";
    public string ExitLyricsOnly => Language == UiLanguage.English ? "Exit lyrics-only" : "退出仅歌词模式";
    public string RescanLyrics => Language == UiLanguage.English ? "Rescan lyrics" : "重新扫描歌词";
    public string Exit => Language == UiLanguage.English ? "Exit" : "退出";
    public string DisplaySettings => Language == UiLanguage.English ? "Display settings" : "显示设置";
    public string ShowTranslation => Language == UiLanguage.English ? "Show translation" : "显示翻译";
    public string LyricsOnlyWindow => Language == UiLanguage.English ? "Lyrics-only window" : "仅歌词窗口";
    public string VerticalLyrics => Language == UiLanguage.English ? "Vertical lyrics" : "竖向歌词";
    public string DynamicIsland => Language == UiLanguage.English ? "Dynamic island" : "灵动岛模式";
    public string AutoScrollLongLyrics => Language == UiLanguage.English ? "Auto scroll long lyrics" : "长歌词自动滚动";
    public string AutoCenterCurrentLyric => Language == UiLanguage.English ? "Center current lyric" : "当前歌词自动居中";
    public string AutoOpenLyricsPanel => Language == UiLanguage.English ? "Auto open Apple Music lyrics" : "自动打开 Apple Music 歌词";
    public string AutoContrastText => Language == UiLanguage.English ? "Auto contrast text" : "自动调整文字对比度";
    public string InterfaceLanguage => Language == UiLanguage.English ? "Interface language" : "界面语言";
    public string LanguageChinese => Language == UiLanguage.English ? "Chinese (Simplified)" : "简体中文";
    public string LanguageEnglish => Language == UiLanguage.English ? "English" : "英文";
    public string LyricsOffset => Language == UiLanguage.English ? "Lyric lead" : "歌词提前量";
    public string LyricsOffsetHint => Language == UiLanguage.English ? "Higher values show lines earlier." : "数值越大，歌词显示越早。";
    public string MainTextSize => Language == UiLanguage.English ? "Main text size" : "主歌词字号";
    public string OriginalTextSize => Language == UiLanguage.English ? "Original text size" : "原文字号";
    public string BackgroundOpacity => Language == UiLanguage.English ? "Background opacity" : "背景透明度";
    public string LyricPositionX => Language == UiLanguage.English ? "Lyric horizontal position" : "歌词横向位置";
    public string LyricPositionY => Language == UiLanguage.English ? "Lyric vertical position" : "歌词纵向位置";
    public string IslandWidth => Language == UiLanguage.English ? "Island width" : "灵动岛宽度";
    public string IslandHeight => Language == UiLanguage.English ? "Island height" : "灵动岛高度";
    public string IslandTopOffset => Language == UiLanguage.English ? "Island top offset" : "灵动岛顶部间距";
    public string MainColor => Language == UiLanguage.English ? "Main color" : "主歌词颜色";
    public string OriginalColor => Language == UiLanguage.English ? "Original color" : "原文颜色";
    public string BackgroundColor => Language == UiLanguage.English ? "Background color" : "背景颜色";
    public string AccentColor => Language == UiLanguage.English ? "Accent color" : "强调色";
    public string Minimize => Language == UiLanguage.English ? "Minimize" : "最小化";
    public string HideWindow => Language == UiLanguage.English ? "Hide window" : "隐藏窗口";
    public string CloseSettings => Language == UiLanguage.English ? "Close settings" : "关闭设置";
    public string RescanLyricsTooltip => Language == UiLanguage.English ? "Rescan lyrics" : "重新扫描歌词";
    public string SettingsTooltip => Language == UiLanguage.English ? "Open settings" : "打开设置";
    public string TrackDisplay(string title, string artist) => $"{title} - {artist}";
    public string LyricOffsetValue(int value) => Language == UiLanguage.English ? $"{value} ms early" : $"提前 {value} 毫秒";
    public string LyricPositionValue(double value) => Language == UiLanguage.English ? $"{value:0}px" : $"{value:0} 像素";
    public string PixelValue(double value) => Language == UiLanguage.English ? $"{value:0}px" : $"{value:0} 像素";

    public string SourceFor(string source)
    {
        var cached = LyricsCacheService.IsCachedSource(source);
        var cleanSource = cached ? source[..^LyricsCacheService.CachedSuffix.Length].TrimEnd() : source;
        var suffix = cached
            ? Language == UiLanguage.English ? " (cache)" : "（缓存）"
            : string.Empty;

        if (string.Equals(cleanSource, LyricsBundle.AppleMusicMemoryTtmlSource, StringComparison.OrdinalIgnoreCase))
        {
            return AppleMusicMemorySource + suffix;
        }

        if (string.Equals(cleanSource, LyricsBundle.AppleMusicVisibleLyricsSource, StringComparison.OrdinalIgnoreCase))
        {
            return (Language == UiLanguage.English ? "Apple Music visible lyrics" : "Apple Music 可见歌词") + suffix;
        }

        if (string.Equals(cleanSource, LyricsBundle.LrcLibSyncedFallbackSource, StringComparison.OrdinalIgnoreCase))
        {
            return LrcLibSyncedFallbackSource + suffix;
        }

        if (string.Equals(cleanSource, LyricsBundle.LrcLibPlainFallbackSource, StringComparison.OrdinalIgnoreCase)
            || string.Equals(cleanSource, LyricsBundle.LrcLibFallbackSource, StringComparison.OrdinalIgnoreCase))
        {
            return LrcLibFallbackSource + suffix;
        }

        return source;
    }
}
