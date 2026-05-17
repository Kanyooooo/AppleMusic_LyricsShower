namespace AppleMusicTranslator.Models;

public sealed record LyricMatch(LyricsBundle Lyrics, double Score, int AnchorHits);
