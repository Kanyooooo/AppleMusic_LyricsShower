using System.IO;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using AppleMusicTranslator.Models;

namespace AppleMusicTranslator.Services;

public sealed class LyricsTranslationService
{
    private const string CacheVersion = "v5";
    private readonly HttpClient _httpClient;
    private readonly string _cachePath;
    private readonly Dictionary<string, TranslationCacheEntry> _cache;
    private readonly Dictionary<string, DateTime> _retryAfterByKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _noDisplayTranslation = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTime _nextRequestUtc = DateTime.MinValue;

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
                MaxDegreeOfParallelism = 2,
                CancellationToken = cancellationToken
            },
            async (text, token) =>
            {
                var translated = await TranslateLineAsync(text, targetLanguage, token);
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    lock (translations)
                    {
                        translations[text] = translated;
                    }

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
                translations.TryGetValue(line.Text, out var translation) && !string.IsNullOrWhiteSpace(translation)
                    ? translation
                    : line.Text))
            .ToArray();

        return new TranslatedLyricsBundle(track, lyrics.Source, translatedLines);
    }

    public static string CreateCacheKey(string text, UiLanguage targetLanguage) =>
        CacheKey(text, targetLanguage);

    public bool TryGetCachedTranslation(string text, UiLanguage targetLanguage, out string translation)
    {
        translation = string.Empty;
        if (ShouldSkipTranslation(text, targetLanguage))
        {
            return false;
        }

        var key = CacheKey(text, targetLanguage);
        lock (_cache)
        {
            if (_noDisplayTranslation.Contains(key))
            {
                return false;
            }

            if (!_cache.TryGetValue(key, out var cached) || !cached.Matches(text, targetLanguage))
            {
                return false;
            }

            if (IsBadTranslation(text, cached.Translation, targetLanguage, cached.SourceLanguage))
            {
                _cache.Remove(key);
                return false;
            }

            translation = cached.Translation;
            return true;
        }
    }

    public async Task<string> TranslateLineAsync(string text, UiLanguage targetLanguage, CancellationToken cancellationToken)
    {
        var key = CacheKey(text, targetLanguage);
        if (ShouldSkipTranslation(text, targetLanguage))
        {
            lock (_cache)
            {
                _noDisplayTranslation.Add(key);
            }

            return string.Empty;
        }

        lock (_cache)
        {
            if (_noDisplayTranslation.Contains(key))
            {
                return string.Empty;
            }

            if (_retryAfterByKey.TryGetValue(key, out var retryAfter) && retryAfter > DateTime.UtcNow)
            {
                return string.Empty;
            }

            if (_cache.TryGetValue(key, out var cached))
            {
                if (!cached.Matches(text, targetLanguage)
                    || IsBadTranslation(text, cached.Translation, targetLanguage, cached.SourceLanguage))
                {
                    _cache.Remove(key);
                }
                else
                {
                    return cached.Translation;
                }
            }
        }

        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var delay = _nextRequestUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            var shouldRetryLater = false;
            var translated = string.Empty;
            var sourceLanguage = string.Empty;

            try
            {
                var google = await TranslateWithGoogleAsync(text, targetLanguage, cancellationToken);
                shouldRetryLater |= google.RateLimited;
                translated = google.Text;
                sourceLanguage = google.SourceLanguage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                shouldRetryLater = true;
            }

            if (IsBadTranslation(text, translated, targetLanguage, sourceLanguage))
            {
                translated = string.Empty;
                sourceLanguage = string.Empty;
                try
                {
                    var myMemory = await TranslateWithMyMemoryAsync(text, targetLanguage, cancellationToken);
                    shouldRetryLater |= myMemory.RateLimited;
                    translated = myMemory.Text;
                    sourceLanguage = myMemory.SourceLanguage;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    shouldRetryLater = true;
                }
            }

            if (IsBadTranslation(text, translated, targetLanguage, sourceLanguage))
            {
                if (shouldRetryLater)
                {
                    MarkRetry(key, TimeSpan.FromSeconds(25));
                }
                else
                {
                    MarkNoDisplayTranslation(key);
                }

                return string.Empty;
            }

            lock (_cache)
            {
                _cache[key] = TranslationCacheEntry.Create(text, targetLanguage, translated, sourceLanguage);
            }

            return translated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            MarkRetry(key, TimeSpan.FromSeconds(20));
            return string.Empty;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public void FlushCache()
    {
        lock (_cache)
        {
            SaveCache();
        }
    }

    private async Task<TranslationResponse> TranslateWithGoogleAsync(
        string text,
        UiLanguage targetLanguage,
        CancellationToken cancellationToken)
    {
        _nextRequestUtc = DateTime.UtcNow.AddMilliseconds(850);

        var uri = "https://translate.googleapis.com/translate_a/single"
            + "?client=gtx&sl=auto&tl=" + TargetLanguageCode(targetLanguage)
            + "&dt=t&q=" + Uri.EscapeDataString(text);

        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _nextRequestUtc = DateTime.UtcNow.AddSeconds(8);
            return new TranslationResponse(string.Empty, true, string.Empty);
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return new TranslationResponse(
            ReadGoogleTranslation(document.RootElement).Trim(),
            false,
            ReadGoogleSourceLanguage(document.RootElement));
    }

    private async Task<TranslationResponse> TranslateWithMyMemoryAsync(
        string text,
        UiLanguage targetLanguage,
        CancellationToken cancellationToken)
    {
        _nextRequestUtc = DateTime.UtcNow.AddMilliseconds(1100);

        var sourceLanguage = SourceLanguageCode(text, targetLanguage);
        var uri = "https://api.mymemory.translated.net/get"
            + "?q=" + Uri.EscapeDataString(text)
            + "&langpair=" + Uri.EscapeDataString($"{sourceLanguage}|{TargetLanguageCode(targetLanguage)}");

        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _nextRequestUtc = DateTime.UtcNow.AddSeconds(12);
            return new TranslationResponse(string.Empty, true, string.Empty);
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return new TranslationResponse(
            WebUtility.HtmlDecode(ReadMyMemoryTranslation(document.RootElement).Trim()),
            false,
            sourceLanguage);
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

    private static string ReadGoogleSourceLanguage(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array
            && root.GetArrayLength() > 2
            && root[2].ValueKind == JsonValueKind.String)
        {
            return root[2].GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string ReadMyMemoryTranslation(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("responseData", out var responseData)
            || responseData.ValueKind != JsonValueKind.Object
            || !responseData.TryGetProperty("translatedText", out var translated)
            || translated.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return translated.GetString() ?? string.Empty;
    }

    private static bool ShouldSkipTranslation(string text, UiLanguage targetLanguage)
    {
        var profile = AnalyzeScript(text);
        if (profile.CjkLetters == 0
            && profile.KanaLetters == 0
            && profile.HangulLetters == 0
            && profile.LatinLetters == 0)
        {
            return true;
        }

        return IsLikelySameLanguage(text, targetLanguage, profile);
    }

    private void MarkRetry(string key, TimeSpan delay)
    {
        lock (_cache)
        {
            _retryAfterByKey[key] = DateTime.UtcNow.Add(delay);
        }
    }

    private void MarkNoDisplayTranslation(string key)
    {
        lock (_cache)
        {
            _noDisplayTranslation.Add(key);
        }
    }

    private static bool IsBadTranslation(
        string original,
        string translated,
        UiLanguage targetLanguage,
        string sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(translated))
        {
            return true;
        }

        if (string.Equals(NormalizeText(original), NormalizeText(translated), StringComparison.OrdinalIgnoreCase))
        {
            return !SourceLanguageMatchesTarget(original, targetLanguage, sourceLanguage);
        }

        var translatedProfile = AnalyzeScript(translated);
        if (targetLanguage == UiLanguage.ChineseSimplified)
        {
            return translatedProfile.CjkLetters == 0
                || translatedProfile.KanaLetters > 0
                || translatedProfile.HangulLetters > 0;
        }

        if (targetLanguage == UiLanguage.English)
        {
            return translatedProfile.LatinLetters == 0
                || translatedProfile.CjkLetters > 0
                || translatedProfile.KanaLetters > 0
                || translatedProfile.HangulLetters > 0;
        }

        return false;
    }

    private static bool SourceLanguageMatchesTarget(string original, UiLanguage targetLanguage, string sourceLanguage)
    {
        if (!string.IsNullOrWhiteSpace(sourceLanguage) && !IsAutoLanguageCode(sourceLanguage))
        {
            return LanguageCodeMatchesTarget(sourceLanguage, targetLanguage);
        }

        return IsLikelySameLanguage(original, targetLanguage, AnalyzeScript(original));
    }

    private static bool LanguageCodeMatchesTarget(string sourceLanguage, UiLanguage targetLanguage)
    {
        var normalized = sourceLanguage.Trim().ToLowerInvariant().Replace('_', '-');
        return targetLanguage switch
        {
            UiLanguage.English => normalized == "en" || normalized.StartsWith("en-", StringComparison.Ordinal),
            UiLanguage.ChineseSimplified => normalized is "zh" or "zh-cn" or "zh-hans"
                || normalized.StartsWith("zh-cn-", StringComparison.Ordinal)
                || normalized.StartsWith("zh-hans-", StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool IsAutoLanguageCode(string sourceLanguage)
    {
        var normalized = sourceLanguage.Trim().ToLowerInvariant();
        return normalized is "auto" or "autodetect";
    }

    private static bool IsLikelySameLanguage(string text, UiLanguage targetLanguage, ScriptProfile profile)
    {
        if (targetLanguage == UiLanguage.ChineseSimplified)
        {
            return IsLikelyChineseSentence(text, profile)
                && profile.CjkLetters > 0
                && profile.KanaLetters == 0
                && profile.HangulLetters == 0
                && profile.LatinLetters == 0;
        }

        return profile.LatinLetters > 0
            && profile.CjkLetters == 0
            && profile.KanaLetters == 0
            && profile.HangulLetters == 0;
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

    private static bool IsLikelyChineseSentence(string text, ScriptProfile profile)
    {
        if (profile.CjkLetters == 0 || profile.KanaLetters > 0 || profile.HangulLetters > 0)
        {
            return false;
        }

        var compact = NormalizeText(text);
        if (compact.Length == 0)
        {
            return false;
        }

        var commonChineseMarkers = new[]
        {
            '的', '了', '是', '在', '我', '你', '他', '她', '它', '们',
            '这', '那', '吗', '吧', '呢', '啊', '把', '被', '着', '过',
            '给', '就', '都', '也', '还', '和', '与', '让', '没', '有',
            '不', '会', '想', '要', '能', '说', '看', '听', '爱'
        };

        if (compact.Any(commonChineseMarkers.Contains))
        {
            return true;
        }

        var simplifiedOnlyMarkers = new[]
        {
            '这', '们', '吗', '过', '还', '听', '爱', '让', '说', '会',
            '来', '见', '边', '间', '欢', '为', '无', '发', '当', '从'
        };

        return compact.Any(simplifiedOnlyMarkers.Contains);
    }

    private static string TargetLanguageCode(UiLanguage language) =>
        language == UiLanguage.English ? "en" : "zh-CN";

    private static string SourceLanguageCode(string text, UiLanguage targetLanguage)
    {
        var profile = AnalyzeScript(text);
        if (profile.KanaLetters > 0)
        {
            return "ja";
        }

        if (profile.HangulLetters > 0)
        {
            return "ko";
        }

        if (profile.CjkLetters > 0 && targetLanguage == UiLanguage.English)
        {
            return "zh-CN";
        }

        if (profile.LatinLetters > 0 && profile.CjkLetters == 0)
        {
            return "en";
        }

        return "autodetect";
    }

    private static string CacheKey(string text, UiLanguage targetLanguage) =>
        string.Join('|', CacheVersion, TargetLanguageCode(targetLanguage), EncodeCacheText(text));

    private static string NormalizeText(string text) =>
        string.Concat((text ?? string.Empty)
            .Normalize(NormalizationForm.FormKC)
            .Where(ch => !char.IsWhiteSpace(ch)))
            .Trim();

    private static string EncodeCacheText(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? string.Empty))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static Dictionary<string, TranslationCacheEntry> LoadCache(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, TranslationCacheEntry>(StringComparer.Ordinal);
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<Dictionary<string, TranslationCacheEntry>>(json)
                ?.Where(pair => IsCurrentCacheKey(pair.Key) && pair.Value.IsValid())
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, TranslationCacheEntry>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, TranslationCacheEntry>(StringComparer.Ordinal);
        }
    }

    private static bool IsCurrentCacheKey(string key) =>
        key.StartsWith(CacheVersion + "|", StringComparison.Ordinal);

    private void SaveCache()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            WriteAllTextAtomic(_cachePath, json);
        }
        catch
        {
            // Cache write failure should never break lyrics display.
        }
    }

    private static void WriteAllTextAtomic(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, text, Encoding.UTF8);
        File.Move(tempPath, path, overwrite: true);
    }

    private sealed class ScriptProfile
    {
        public int CjkLetters { get; set; }

        public int ChineseLetters { get; set; }

        public int KanaLetters { get; set; }

        public int HangulLetters { get; set; }

        public int LatinLetters { get; set; }
    }

    private readonly record struct TranslationResponse(string Text, bool RateLimited, string SourceLanguage);

    private sealed class TranslationCacheEntry
    {
        public string Version { get; set; } = string.Empty;

        public string TargetLanguage { get; set; } = string.Empty;

        public string OriginalText { get; set; } = string.Empty;

        public string Translation { get; set; } = string.Empty;

        public string SourceLanguage { get; set; } = string.Empty;

        public static TranslationCacheEntry Create(
            string originalText,
            UiLanguage targetLanguage,
            string translation,
            string sourceLanguage) =>
            new()
            {
                Version = CacheVersion,
                TargetLanguage = TargetLanguageCode(targetLanguage),
                OriginalText = originalText,
                Translation = translation,
                SourceLanguage = sourceLanguage ?? string.Empty
            };

        public bool Matches(string originalText, UiLanguage targetLanguage) =>
            IsValid()
            && string.Equals(Version, CacheVersion, StringComparison.Ordinal)
            && string.Equals(TargetLanguage, TargetLanguageCode(targetLanguage), StringComparison.Ordinal)
            && string.Equals(OriginalText, originalText, StringComparison.Ordinal);

        public bool IsValid() =>
            !string.IsNullOrWhiteSpace(Version)
            && !string.IsNullOrWhiteSpace(TargetLanguage)
            && !string.IsNullOrWhiteSpace(OriginalText)
            && !string.IsNullOrWhiteSpace(Translation);
    }
}
