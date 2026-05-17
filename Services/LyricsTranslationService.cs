using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using AppleMusicTranslator.Models;

namespace AppleMusicTranslator.Services;

public sealed class LyricsTranslationService
{
    private const string TargetLanguage = "zh-CN";
    private readonly HttpClient _httpClient;
    private readonly string _cachePath;
    private readonly Dictionary<string, string> _cache;

    public LyricsTranslationService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AppleMusicTranslator/0.1");

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AppleMusicTranslator");
        Directory.CreateDirectory(appData);

        _cachePath = Path.Combine(appData, "translation-cache.json");
        _cache = LoadCache(_cachePath);
    }

    public async Task<TranslatedLyricsBundle> TranslateAsync(
        TrackInfo track,
        LyricsBundle lyrics,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (lyrics.Lines.Count == 0)
        {
            return TranslatedLyricsBundle.Empty(track, lyrics.Source);
        }

        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        var distinctTexts = lyrics.Lines
            .Select(line => line.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var completed = 0;
        var dirty = 0;

        await Parallel.ForEachAsync(
            distinctTexts,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 6,
                CancellationToken = cancellationToken
            },
            async (text, token) =>
            {
                var translated = await TranslateLineAsync(text, token);
                lock (translations)
                {
                    translations[text] = translated;
                }

                if (translated != text)
                {
                    Interlocked.Exchange(ref dirty, 1);
                }

                var done = Interlocked.Increment(ref completed);
                progress?.Report($"正在翻译歌词 {done}/{distinctTexts.Length}");
            });

        if (dirty == 1)
        {
            lock (_cache)
            {
                SaveCache();
            }
        }

        var translatedLines = lyrics.Lines
            .Select(line => new TranslatedLyricLine(
                line.Begin,
                line.End,
                line.Text,
                translations.TryGetValue(line.Text, out var translation) ? translation : line.Text))
            .ToArray();

        return new TranslatedLyricsBundle(track, lyrics.Source, translatedLines);
    }

    public bool TryGetCachedTranslation(string text, out string translation)
    {
        var key = CacheKey(text);
        lock (_cache)
        {
            return _cache.TryGetValue(key, out translation!);
        }
    }

    public async Task<string> TranslateLineAsync(string text, CancellationToken cancellationToken)
    {
        var key = CacheKey(text);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        try
        {
            var uri = "https://translate.googleapis.com/translate_a/single"
                + "?client=gtx&sl=auto&tl=" + TargetLanguage
                + "&dt=t&q=" + Uri.EscapeDataString(text);

            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var translated = ReadGoogleTranslation(document.RootElement);

            if (string.IsNullOrWhiteSpace(translated))
            {
                return text;
            }

            translated = translated.Trim();
            lock (_cache)
            {
                _cache[key] = translated;
            }

            return translated;
        }
        catch
        {
            return text;
        }
    }

    public void FlushCache()
    {
        lock (_cache)
        {
            SaveCache();
        }
    }

    private static string ReadGoogleTranslation(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var segments = root[0];
        if (segments.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array
                && segment.GetArrayLength() > 0
                && segment[0].ValueKind == JsonValueKind.String)
            {
                builder.Append(segment[0].GetString());
            }
        }

        return builder.ToString();
    }

    private static string CacheKey(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{TargetLanguage}\n{text}"));
        return Convert.ToHexString(bytes);
    }

    private static Dictionary<string, string> LoadCache(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void SaveCache()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cachePath, json, Encoding.UTF8);
        }
        catch
        {
            // Cache write failure should never break lyrics display.
        }
    }
}
