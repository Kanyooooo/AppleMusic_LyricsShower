using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppleMusicTranslator.Models;

namespace AppleMusicTranslator.Services;

public sealed class LyricsCacheService
{
    public const string CachedSuffix = " cache";

    private readonly object _lock = new();
    private readonly string _cachePath;
    private CacheFile _cache;

    public LyricsCacheService()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AppleMusicTranslator");
        Directory.CreateDirectory(appData);

        _cachePath = Path.Combine(appData, "lyrics-cache.json");
        _cache = LoadCache(_cachePath);
    }

    public bool TryGet(TrackInfo track, out LyricsCacheResult result)
    {
        lock (_lock)
        {
            if (!_cache.Tracks.TryGetValue(track.CacheKey, out var entry))
            {
                result = new LyricsCacheResult(LyricsBundle.Empty("No cached lyrics"), false, string.Empty);
                return false;
            }

            var lyrics = ToBundle(entry).WithSource($"{entry.Source}{CachedSuffix}");
            result = new LyricsCacheResult(
                lyrics,
                HasConflictLocked(track.CacheKey, entry.Fingerprint, out var conflict),
                conflict);
            return true;
        }
    }

    public bool HasConflict(TrackInfo track, LyricsBundle lyrics, out string conflictDescription)
    {
        var fingerprint = Fingerprint(lyrics);
        lock (_lock)
        {
            return HasConflictLocked(track.CacheKey, fingerprint, out conflictDescription);
        }
    }

    public void Save(TrackInfo track, LyricsBundle lyrics)
    {
        if (!ShouldCache(lyrics))
        {
            return;
        }

        lock (_lock)
        {
            _cache.Tracks[track.CacheKey] = new CacheEntry
            {
                Title = track.Title,
                Artist = track.CanonicalArtist,
                Album = track.CanonicalAlbum,
                DurationMs = (long)Math.Round(track.Duration.TotalMilliseconds),
                Source = StripCacheSuffix(lyrics.Source),
                IsSynced = lyrics.IsSynced,
                CapturedAtUtc = DateTime.UtcNow,
                Fingerprint = Fingerprint(lyrics),
                Lines = lyrics.Lines
                    .Select(line => new CacheLine
                    {
                        BeginMs = (long)Math.Round(line.Begin.TotalMilliseconds),
                        EndMs = (long)Math.Round(line.End.TotalMilliseconds),
                        Text = line.Text
                    })
                    .ToList()
            };

            PruneLocked();
            SaveCacheLocked();
        }
    }

    public bool Delete(TrackInfo track)
    {
        lock (_lock)
        {
            var removed = _cache.Tracks.Remove(track.CacheKey);
            if (removed)
            {
                SaveCacheLocked();
            }

            return removed;
        }
    }

    public string CachePath => _cachePath;

    public static bool IsCachedSource(string source) =>
        source.EndsWith(CachedSuffix, StringComparison.OrdinalIgnoreCase);

    private static bool ShouldCache(LyricsBundle lyrics) =>
        lyrics.IsSynced
        && lyrics.Lines.Count >= 4
        && !IsCachedSource(lyrics.Source)
        && !string.Equals(lyrics.Source, LyricsBundle.AppleMusicVisibleLyricsSource, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(lyrics.Source, LyricsBundle.LrcLibPlainFallbackSource, StringComparison.OrdinalIgnoreCase);

    private bool HasConflictLocked(string currentTrackKey, string fingerprint, out string conflictDescription)
    {
        conflictDescription = string.Empty;
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return false;
        }

        var conflict = _cache.Tracks
            .Where(item => !string.Equals(item.Key, currentTrackKey, StringComparison.Ordinal)
                && string.Equals(item.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
            .Select(item => item.Value)
            .OrderByDescending(item => item.CapturedAtUtc)
            .FirstOrDefault();

        if (conflict is null)
        {
            return false;
        }

        conflictDescription = $"{conflict.Title} - {conflict.Artist}";
        return true;
    }

    private static LyricsBundle ToBundle(CacheEntry entry)
    {
        var lines = entry.Lines
            .Select(line => new LyricLine(
                TimeSpan.FromMilliseconds(Math.Max(0, line.BeginMs)),
                TimeSpan.FromMilliseconds(Math.Max(line.BeginMs + 1000, line.EndMs)),
                line.Text ?? string.Empty))
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .ToArray();

        return new LyricsBundle(
            entry.Source,
            lines,
            entry.IsSynced,
            TimeSpan.FromMilliseconds(Math.Max(0, entry.DurationMs)),
            string.Join('\n', lines.Select(line => line.Text)));
    }

    private static string Fingerprint(LyricsBundle lyrics)
    {
        var text = string.Join('\n', lyrics.Lines
            .Select(line => NormalizeLyricText(line.Text))
            .Where(line => line.Length > 0)
            .Take(40));

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string NormalizeLyricText(string text) =>
        string.Concat((text ?? string.Empty)
            .Where(ch => !char.IsWhiteSpace(ch)))
            .Trim()
            .ToLowerInvariant();

    private static string StripCacheSuffix(string source) =>
        IsCachedSource(source)
            ? source[..^CachedSuffix.Length].TrimEnd()
            : source;

    private static CacheFile LoadCache(string path)
    {
        if (!File.Exists(path))
        {
            return new CacheFile();
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<CacheFile>(json) ?? new CacheFile();
        }
        catch
        {
            return new CacheFile();
        }
    }

    private void SaveCacheLocked()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cachePath, json, Encoding.UTF8);
        }
        catch
        {
            // Lyrics cache is an acceleration layer. It must never break live scanning.
        }
    }

    private void PruneLocked()
    {
        if (_cache.Tracks.Count <= 300)
        {
            return;
        }

        foreach (var key in _cache.Tracks
                     .OrderBy(item => item.Value.CapturedAtUtc)
                     .Take(_cache.Tracks.Count - 300)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _cache.Tracks.Remove(key);
        }
    }

    private sealed class CacheFile
    {
        public int Version { get; set; } = 1;

        public Dictionary<string, CacheEntry> Tracks { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class CacheEntry
    {
        public string Title { get; set; } = string.Empty;

        public string Artist { get; set; } = string.Empty;

        public string Album { get; set; } = string.Empty;

        public long DurationMs { get; set; }

        public string Source { get; set; } = string.Empty;

        public bool IsSynced { get; set; }

        public DateTime CapturedAtUtc { get; set; }

        public string Fingerprint { get; set; } = string.Empty;

        public List<CacheLine> Lines { get; set; } = [];
    }

    private sealed class CacheLine
    {
        public long BeginMs { get; set; }

        public long EndMs { get; set; }

        public string? Text { get; set; }
    }
}
