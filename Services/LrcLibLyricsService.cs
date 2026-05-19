using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppleMusicTranslator.Models;

namespace AppleMusicTranslator.Services;

public sealed partial class LrcLibLyricsService
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public async Task<LyricsBundle> SearchAsync(TrackInfo track, CancellationToken cancellationToken)
    {
        if (!track.HasSongIdentity)
        {
            return LyricsBundle.Empty("No current song");
        }

        var title = NormalizeTrackText(track.Title);
        var artists = BuildArtistQueries(track.CanonicalArtist).ToArray();
        var albums = BuildAlbumQueries(track.CanonicalAlbum).ToArray();

        foreach (var artist in artists)
        {
            foreach (var album in albums.DefaultIfEmpty(string.Empty))
            {
                var query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
                if (!string.IsNullOrWhiteSpace(album))
                {
                    query += $"&album_name={Uri.EscapeDataString(album)}";
                }

                if (track.Duration > TimeSpan.Zero)
                {
                    query += $"&duration={(int)Math.Round(track.Duration.TotalSeconds)}";
                }

                try
                {
                    using var response = await _httpClient.GetAsync($"https://lrclib.net/api/get?{query}", cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    var root = document.RootElement;
                    if (!LooksLikeRequestedTrack(root, track))
                    {
                        continue;
                    }

                    var synced = ReadString(root, "syncedLyrics");
                    if (!string.IsNullOrWhiteSpace(synced))
                    {
                        var lines = ParseSyncedLyrics(synced);
                        if (lines.Count > 0)
                        {
                            return new LyricsBundle(LyricsBundle.LrcLibSyncedFallbackSource, lines, true, track.Duration, string.Empty);
                        }
                    }

                    var plain = ReadString(root, "plainLyrics");
                    if (!string.IsNullOrWhiteSpace(plain))
                    {
                        var lines = ParsePlainLyrics(plain);
                        if (lines.Count > 0)
                        {
                            return new LyricsBundle(LyricsBundle.LrcLibPlainFallbackSource, lines, false, track.Duration, string.Empty);
                        }
                    }
                }
                catch
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Try the next artist/album permutation.
                }
            }
        }

        return LyricsBundle.Empty("LRCLIB: not found");
    }

    private static IEnumerable<string> BuildArtistQueries(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in ArtistSeparatorRegex().Split(artist.Trim()).Prepend(artist.Trim()))
        {
            var clean = part.Trim();
            if (clean.Length >= 2 && seen.Add(clean))
            {
                yield return clean;
            }
        }
    }

    private static IEnumerable<string> BuildAlbumQueries(string album)
    {
        if (string.IsNullOrWhiteSpace(album))
        {
            yield break;
        }

        var cleaned = album.Trim();
        yield return cleaned;
    }

    private static string NormalizeTrackText(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool LooksLikeRequestedTrack(JsonElement root, TrackInfo track)
    {
        var responseTitle = ReadString(root, "trackName") ?? string.Empty;
        var responseArtist = ReadString(root, "artistName") ?? string.Empty;
        var responseAlbum = ReadString(root, "albumName") ?? string.Empty;

        if (!IsSimilarText(responseTitle, track.Title))
        {
            return false;
        }

        if (!ContainsAnyToken(responseArtist, track.CanonicalArtist))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(track.CanonicalAlbum)
            && !string.IsNullOrWhiteSpace(responseAlbum)
            && !ContainsAnyToken(responseAlbum, track.CanonicalAlbum))
        {
            return false;
        }

        if (track.Duration > TimeSpan.Zero
            && root.TryGetProperty("duration", out var durationProperty)
            && durationProperty.ValueKind == JsonValueKind.Number
            && durationProperty.TryGetDouble(out var responseDuration)
            && Math.Abs(responseDuration - track.Duration.TotalSeconds) > 12)
        {
            return false;
        }

        return true;
    }

    private static bool IsSimilarText(string left, string right)
    {
        var leftNorm = NormalizeComparable(left);
        var rightNorm = NormalizeComparable(right);
        if (leftNorm.Length == 0 || rightNorm.Length == 0)
        {
            return false;
        }

        return leftNorm.Equals(rightNorm, StringComparison.Ordinal)
            || leftNorm.Contains(rightNorm, StringComparison.Ordinal)
            || rightNorm.Contains(leftNorm, StringComparison.Ordinal);
    }

    private static bool ContainsAnyToken(string haystack, string query)
    {
        var normalizedHaystack = NormalizeComparable(haystack);
        var tokens = NormalizeComparable(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 2)
            .ToArray();

        return tokens.Length > 0 && tokens.Any(normalizedHaystack.Contains);
    }

    private static string NormalizeComparable(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();

        return WhitespaceRegex().Replace(new string(chars), " ").Trim();
    }

    private static IReadOnlyList<LyricLine> ParseSyncedLyrics(string lrc)
    {
        var collected = new List<(TimeSpan Time, string Text)>();

        foreach (var rawLine in lrc.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var matches = LrcTimestampRegex().Matches(rawLine);
            if (matches.Count == 0)
            {
                continue;
            }

            var text = LrcTimestampRegex().Replace(rawLine, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (Match match in matches)
            {
                if (TtmlLyricsParser.TryParseTime(match.Groups["time"].Value, out var time))
                {
                    collected.Add((time, text));
                }
            }
        }

        collected.Sort((left, right) => left.Time.CompareTo(right.Time));
        var lines = new List<LyricLine>();
        for (var i = 0; i < collected.Count; i++)
        {
            var begin = collected[i].Time;
            var end = i + 1 < collected.Count ? collected[i + 1].Time : begin + TimeSpan.FromSeconds(5);
            lines.Add(new LyricLine(begin, end, collected[i].Text));
        }

        return lines;
    }

    private static IReadOnlyList<LyricLine> ParsePlainLyrics(string lyrics)
    {
        return lyrics
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select((line, index) =>
            {
                var begin = TimeSpan.FromSeconds(index * 4);
                return new LyricLine(begin, begin + TimeSpan.FromSeconds(4), line.Trim());
            })
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .ToArray();
    }

    [GeneratedRegex("\\[(?<time>\\d+:\\d+(?:\\.\\d+)?)\\]", RegexOptions.Compiled)]
    private static partial Regex LrcTimestampRegex();

    [GeneratedRegex("\\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\s*[\u2013\u2014-]\s*|\s*feat\.?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ArtistSeparatorRegex();
}
