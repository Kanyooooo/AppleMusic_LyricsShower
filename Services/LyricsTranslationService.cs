using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppleMusicTranslator.Models;

namespace AppleMusicTranslator.Services;

public sealed class LyricsTranslationService
{
    private const string CacheVersion = "v2";
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
        UiLanguage targetLanguage,
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
                var translated = await TranslateLineAsync(text, targetLanguage, token);
                lock (translations)
                {
                    translations[text] = translated;
                }

                if (translated != text)
                {
                    Interlocked.Exchange(ref dirty, 1);
                }

                var done = Interlocked.Increment(ref completed);
                progress?.Report(targetLanguage == UiLanguage.English
                    ? $"Translating lyrics {done}/{distinctTexts.Length}"
                    : $"正在翻译歌词 {done}/{distinctTexts.Length}");
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

    public bool TryGetCachedTranslation(string text, UiLanguage targetLanguage, out string translation)
    {
        translation = string.Empty;
        if (ShouldSkipTranslation(text, targetLanguage))
        {
            translation = text;
            return true;
        }

        var key = CacheKey(text, targetLanguage);
        lock (_cache)
        {
            if (!_cache.TryGetValue(key, out var cached) || string.IsNullOrWhiteSpace(cached))
            {
                return false;
            }

            if (IsBadSameLanguageFallback(text, cached, targetLanguage))
            {
                _cache.Remove(key);
                return false;
            }

            translation = cached;
            return true;
        }
    }

    public async Task<string> TranslateLineAsync(string text, UiLanguage targetLanguage, CancellationToken cancellationToken)
    {
        if (ShouldSkipTranslation(text, targetLanguage))
        {
            return text;
        }

        var key = CacheKey(text, targetLanguage);
        lock (_cache)
        {
                if (_cache.TryGetValue(key, out var cached))
                {
                    if (IsBadSameLanguageFallback(text, cached, targetLanguage))
                    {
                        _cache.Remove(key);
                    }
                    else
                    {
                        return cached;
                    }
                }
        }

        try
        {
            var uri = "https://translate.googleapis.com/translate_a/single"
                + "?client=gtx&sl=auto&tl=" + TargetLanguageCode(targetLanguage)
                + "&dt=t&q=" + Uri.EscapeDataString(text);

            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var translated = ReadGoogleTranslation(document.RootElement).Trim();

            if (string.IsNullOrWhiteSpace(translated))
            {
                return text;
            }

            if (IsBadSameLanguageFallback(text, translated, targetLanguage))
            {
                return text;
            }

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

    private static bool ShouldSkipTranslation(string text, UiLanguage targetLanguage)
    {
        var profile = AnalyzeScript(text);
        return targetLanguage == UiLanguage.English
            ? profile.LatinLetters > 0
                && profile.CjkLetters == 0
                && profile.KanaLetters == 0
                && profile.HangulLetters == 0
            : profile.CjkLetters > 0
                && profile.KanaLetters == 0
                && profile.HangulLetters == 0
                && profile.LatinLetters == 0;
    }

    private static bool IsBadSameLanguageFallback(string original, string translated, UiLanguage targetLanguage)
    {
        if (!string.Equals(NormalizeText(original), NormalizeText(translated), StringComparison.Ordinal))
        {
            return false;
        }

        return !ShouldSkipTranslation(original, targetLanguage);
    }

    private static ScriptProfile AnalyzeScript(string text)
    {
        var profile = new ScriptProfile();
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (IsCjkUnified(value))
            {
                profile.CjkLetters++;
                if (IsProbablyChineseHan(value))
                {
                    profile.ChineseLetters++;
                }
            }
            else if (IsKana(value))
            {
                profile.KanaLetters++;
            }
            else if (IsHangul(value))
            {
                profile.HangulLetters++;
            }
            else if (IsLatinLetter(value))
            {
                profile.LatinLetters++;
            }
        }

        return profile;
    }

    private static bool IsCjkUnified(int value) =>
        value is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2FA1F;

    private static bool IsProbablyChineseHan(int value)
    {
        var ch = char.ConvertFromUtf32(value);
        return ch.Any(character => character is >= '\u4E00' and <= '\u9FFF');
    }

    private static bool IsKana(int value) =>
        value is >= 0x3040 and <= 0x30FF
            or >= 0x31F0 and <= 0x31FF
            or >= 0xFF66 and <= 0xFF9F;

    private static bool IsHangul(int value) =>
        value is >= 0xAC00 and <= 0xD7AF
            or >= 0x1100 and <= 0x11FF
            or >= 0x3130 and <= 0x318F;

    private static bool IsLatinLetter(int value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static string TargetLanguageCode(UiLanguage language) =>
        language == UiLanguage.English ? "en" : "zh-CN";

    private static string CacheKey(string text, UiLanguage targetLanguage)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{CacheVersion}\n{TargetLanguageCode(targetLanguage)}\n{text}"));
        return Convert.ToHexString(bytes);
    }

    private static string NormalizeText(string text) =>
        string.Concat((text ?? string.Empty).Where(ch => !char.IsWhiteSpace(ch))).Trim();

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

    private sealed class ScriptProfile
    {
        public int CjkLetters { get; set; }

        public int ChineseLetters { get; set; }

        public int KanaLetters { get; set; }

        public int HangulLetters { get; set; }

        public int LatinLetters { get; set; }
    }
}
