namespace AppleMusicTranslator.Models;

public sealed record TrackInfo(
    string Title,
    string Artist,
    string Album,
    TimeSpan Position,
    TimeSpan Duration,
    bool IsPlaying)
{
    public static TrackInfo Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        TimeSpan.Zero,
        TimeSpan.Zero,
        false);

    public string CacheKey =>
        $"{CanonicalArtist.Trim().ToLowerInvariant()}::{Title.Trim().ToLowerInvariant()}::{CanonicalAlbum.Trim().ToLowerInvariant()}";

    public string CanonicalArtist => SplitArtistAlbum(Artist, Album).Artist;

    public string CanonicalAlbum => SplitArtistAlbum(Artist, Album).Album;

    public bool HasSongIdentity =>
        !string.IsNullOrWhiteSpace(Title)
        && !string.IsNullOrWhiteSpace(Artist);

    private static (string Artist, string Album) SplitArtistAlbum(string artist, string album)
    {
        if (!string.IsNullOrWhiteSpace(album))
        {
            return (artist, album);
        }

        var parts = artist.Split(
            [" \u2014 ", " \u2013 ", " - "],
            2,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return parts.Length == 2
            ? (parts[0], parts[1])
            : (artist, album);
    }
}
