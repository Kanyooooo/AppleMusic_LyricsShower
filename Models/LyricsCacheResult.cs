namespace AppleMusicTranslator.Models;

public sealed record LyricsCacheResult(
    LyricsBundle Lyrics,
    double Confidence,
    bool HasConflict,
    string ConflictDescription);
