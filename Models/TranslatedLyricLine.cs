namespace AppleMusicTranslator.Models;

public sealed record TranslatedLyricLine(
    TimeSpan Begin,
    TimeSpan End,
    string Original,
    string Translation);
