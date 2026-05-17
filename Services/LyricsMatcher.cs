using AppleMusicTranslator.Models;

namespace AppleMusicTranslator.Services;

public sealed class LyricsMatcher
{
    public LyricMatch? FindBestMatch(
        IEnumerable<LyricsBundle> candidates,
        TrackInfo track,
        IReadOnlyCollection<string>? visibleAnchors = null)
    {
        visibleAnchors ??= Array.Empty<string>();
        var hasAnchors = visibleAnchors.Count > 0;
        var best = candidates
            .Where(candidate => IsViableCandidate(candidate, track, hasAnchors))
            .Select(candidate =>
            {
                var anchorHits = CountAnchorHits(candidate, visibleAnchors);
                return new LyricMatch(candidate, Score(candidate, track, anchorHits, visibleAnchors), anchorHits);
            })
            .Where(match => match.Lyrics.Lines.Count >= 2)
            .OrderByDescending(match => match.Score)
            .Take(2)
            .ToArray();

        if (best.Length == 0)
        {
            return null;
        }

        if (hasAnchors)
        {
            return best[0].Score < 40 ? null : best[0];
        }

        if (best[0].Score < 170)
        {
            return null;
        }

        return best.Length == 1 || best[0].Score - best[1].Score >= 70
            ? best[0]
            : null;
    }

    private static bool IsViableCandidate(LyricsBundle candidate, TrackInfo track, bool hasAnchors)
    {
        if (candidate.Lines.Count < 2)
        {
            return false;
        }

        if (hasAnchors || candidate.Duration <= TimeSpan.Zero || track.Duration <= TimeSpan.Zero)
        {
            return true;
        }

        var durationDiff = Math.Abs((candidate.Duration - track.Duration).TotalSeconds);
        var lastEndDiff = Math.Abs((candidate.Lines[^1].End - track.Duration).TotalSeconds);
        return durationDiff <= 3.0 || lastEndDiff <= 8.0;
    }

    private static double Score(LyricsBundle candidate, TrackInfo track, int anchorHits, IReadOnlyCollection<string> visibleAnchors)
    {
        var score = 0.0;
        var anchorCount = visibleAnchors.Count;
        var hasAnchors = anchorCount > 0;

        if (hasAnchors)
        {
            score += anchorHits > 0 ? 1100 + anchorHits * 300 : -900;
        }

        if (candidate.Duration > TimeSpan.Zero && track.Duration > TimeSpan.Zero)
        {
            var diff = Math.Abs((candidate.Duration - track.Duration).TotalSeconds);
            score += diff switch
            {
                <= 0.6 => 180,
                <= 1.5 => 150,
                <= 3.0 => 105,
                <= 8.0 => 35,
                <= 15.0 => 0,
                _ => -120
            };
        }

        if (track.Duration > TimeSpan.Zero && candidate.Lines.Count > 0)
        {
            var lastEnd = candidate.Lines[^1].End;
            var tailDiff = Math.Abs((lastEnd - track.Duration).TotalSeconds);
            score += tailDiff switch
            {
                <= 2.0 => 75,
                <= 4.0 => 55,
                <= 12.0 => 20,
                <= 25.0 => 0,
                _ => -40
            };
        }

        score += Math.Min(candidate.Lines.Count, 80) * 0.5;

        var titleAffinity = TextAffinity(candidate, track.Title);
        var artistAffinity = TextAffinity(candidate, track.CanonicalArtist);
        var albumAffinity = TextAffinity(candidate, track.CanonicalAlbum);
        score += titleAffinity * (hasAnchors ? 80 : 24);
        score += artistAffinity * (hasAnchors ? 45 : 12);
        score += albumAffinity * (hasAnchors ? 18 : 6);

        if (!hasAnchors)
        {
            if (candidate.Duration > TimeSpan.Zero && track.Duration > TimeSpan.Zero)
            {
                var diff = Math.Abs((candidate.Duration - track.Duration).TotalSeconds);
                if (diff > 8.0)
                {
                    score -= 50;
                }
            }
        }

        score += RawTextAffinity(candidate, track.Title) * (hasAnchors ? 25 : 8);
        score += RawTextAffinity(candidate, track.CanonicalArtist) * (hasAnchors ? 18 : 6);

        return score;
    }

    private static int CountAnchorHits(LyricsBundle candidate, IReadOnlyCollection<string> visibleAnchors)
    {
        if (visibleAnchors.Count == 0)
        {
            return 0;
        }

        var lyricSet = candidate.Lines
            .Select(line => Normalize(line.Text))
            .Where(text => text.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        return visibleAnchors
            .Select(Normalize)
            .Where(anchor => anchor.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Count(anchor => lyricSet.Contains(anchor));
    }

    private static string Normalize(string value) =>
        string.Concat(value.Where(ch => !char.IsWhiteSpace(ch))).Trim();

    private static double TextAffinity(LyricsBundle candidate, string query)
    {
        var chars = query
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .Distinct()
            .ToArray();

        if (chars.Length == 0)
        {
            return 0;
        }

        var haystack = string.Join(' ', candidate.Lines.Select(line => line.Text)).ToLowerInvariant();
        var hits = chars.Count(haystack.Contains);
        return (double)hits / chars.Length;
    }

    private static double RawTextAffinity(LyricsBundle candidate, string query)
    {
        if (string.IsNullOrWhiteSpace(candidate.RawText))
        {
            return 0;
        }

        var chars = query
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .Distinct()
            .ToArray();

        if (chars.Length == 0)
        {
            return 0;
        }

        var haystack = candidate.RawText.ToLowerInvariant();
        var hits = chars.Count(haystack.Contains);
        return (double)hits / chars.Length;
    }
}
