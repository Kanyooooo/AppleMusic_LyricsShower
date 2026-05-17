namespace AppleMusicTranslator.Models;

public sealed record LyricsCacheResult(
    LyricsBundle Lyrics,
    bool HasConflict,
    string ConflictDescription);
