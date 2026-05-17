namespace AppleMusicTranslator.Models;

public sealed record LyricsBundle(
    string Source,
    IReadOnlyList<LyricLine> Lines,
    bool IsSynced,
    TimeSpan Duration,
    string RawText)
{
    public const string AppleMusicMemoryTtmlSource = "Apple Music memory TTML";
    public const string AppleMusicVisibleLyricsSource = "Apple Music visible lyrics";
    public const string LrcLibSyncedFallbackSource = "LRCLIB synced fallback";
    public const string LrcLibPlainFallbackSource = "LRCLIB plain fallback";
    public const string LrcLibFallbackSource = "LRCLIB fallback";

    public static LyricsBundle Empty(string reason) => new(reason, Array.Empty<LyricLine>(), false, TimeSpan.Zero, string.Empty);

    public LyricsBundle WithSource(string source) => this with { Source = source };

    public LyricLine? FindLine(TimeSpan position, TimeSpan offset)
    {
        var adjusted = position + offset;
        return Lines.FirstOrDefault(line => line.Begin <= adjusted && adjusted < line.End);
    }
}
