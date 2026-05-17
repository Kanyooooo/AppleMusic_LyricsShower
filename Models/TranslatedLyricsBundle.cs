namespace AppleMusicTranslator.Models;

public sealed record TranslatedLyricsBundle(
    TrackInfo Track,
    string Source,
    IReadOnlyList<TranslatedLyricLine> Lines)
{
    public static TranslatedLyricsBundle Empty(TrackInfo track, string source) =>
        new(track, source, Array.Empty<TranslatedLyricLine>());

    public TranslatedLyricLine? FindLine(TimeSpan position, TimeSpan offset)
    {
        var adjusted = position + offset;
        return Lines.FirstOrDefault(line => line.Begin <= adjusted && adjusted < line.End);
    }
}
