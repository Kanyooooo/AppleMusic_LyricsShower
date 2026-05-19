using AppleMusicTranslator.Models;

namespace AppleMusicTranslator;

public sealed record LyricDisplayPayload(
    string Original,
    string Main,
    IReadOnlyList<LyricLine> Lines,
    IReadOnlyDictionary<string, string> Translations,
    LyricLine? ActiveLine,
    bool ShowTranslation,
    LyricsLayoutMode LayoutMode);
