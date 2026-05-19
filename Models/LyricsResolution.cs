namespace AppleMusicTranslator.Models;

public enum LyricsResolutionLayer
{
    Cache,
    VisibleAnchors,
    NearAddressScan,
    FullMemoryScan,
    LrcLibFallback
}

public sealed record LyricsResolution(
    LyricsBundle Lyrics,
    LyricsResolutionLayer Layer,
    double Confidence,
    string Detail)
{
    public bool HasLyrics => Lyrics.Lines.Count > 0;

    public static LyricsResolution Empty(LyricsResolutionLayer layer, string reason) =>
        new(LyricsBundle.Empty(reason), layer, 0, reason);
}
