using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AppleMusicTranslator.Models;

namespace AppleMusicTranslator.Services;

public sealed partial class TtmlLyricsParser
{
    public IReadOnlyList<LyricsBundle> ParseBlocks(IEnumerable<byte[]> rawBlocks)
    {
        var bundles = new List<LyricsBundle>();

        foreach (var rawBlock in rawBlocks)
        {
            foreach (var textBlock in SplitTtmlBlocks(DecodeTtml(rawBlock)))
            {
                var bundle = ParseTextBlock(textBlock);
                if (bundle.Lines.Count > 0)
                {
                    bundles.Add(bundle);
                }
            }
        }

        return bundles;
    }

    private static LyricsBundle ParseTextBlock(string text)
    {
        try
        {
            var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
            var bodyDuration = ReadDuration(document);
            var lines = document
                .Descendants()
                .Where(element => element.Name.LocalName.Equals("p", StringComparison.OrdinalIgnoreCase))
                .Select(ParseLine)
                .Where(line => line is not null)
                .Select(line => line!)
                .DistinctBy(line => $"{line.Begin.TotalMilliseconds:0}:{line.Text}")
                .OrderBy(line => line.Begin)
                .ToArray();

            return new LyricsBundle(LyricsBundle.AppleMusicMemoryTtmlSource, lines, true, bodyDuration, text);
        }
        catch
        {
            return LyricsBundle.Empty("TTML parse failed");
        }
    }

    private static LyricLine? ParseLine(XElement paragraph)
    {
        var beginText = AttributeValue(paragraph, "begin");
        if (!TryParseTime(beginText, out var begin))
        {
            return null;
        }

        var endText = AttributeValue(paragraph, "end");
        if (!TryParseTime(endText, out var end))
        {
            var next = AttributeValue(paragraph, "dur");
            end = TryParseTime(next, out var duration) ? begin + duration : begin + TimeSpan.FromSeconds(4);
        }

        var lyricText = NormalizeLyricText(paragraph.Value);
        return string.IsNullOrWhiteSpace(lyricText) ? null : new LyricLine(begin, end, lyricText);
    }

    private static TimeSpan ReadDuration(XDocument document)
    {
        var body = document.Descendants().FirstOrDefault(element => element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        var durationText = body is null ? null : AttributeValue(body, "dur");
        return TryParseTime(durationText, out var duration) ? duration : TimeSpan.Zero;
    }

    private static string? AttributeValue(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attr => attr.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string DecodeTtml(byte[] bytes)
    {
        var text = LooksLikeUtf16Le(bytes)
            ? Encoding.Unicode.GetString(bytes)
            : Encoding.UTF8.GetString(bytes);

        var first = text.IndexOf("<tt", StringComparison.OrdinalIgnoreCase);
        var last = text.LastIndexOf("</tt>", StringComparison.OrdinalIgnoreCase);
        if (first >= 0 && last > first)
        {
            text = text[first..(last + "</tt>".Length)];
        }

        return text.Replace("\0", string.Empty);
    }

    private static bool LooksLikeUtf16Le(byte[] bytes)
    {
        if (bytes.Length < 8)
        {
            return false;
        }

        var nullOddBytes = 0;
        var sampleLength = Math.Min(bytes.Length, 96);
        for (var i = 1; i < sampleLength; i += 2)
        {
            if (bytes[i] == 0)
            {
                nullOddBytes++;
            }
        }

        return nullOddBytes > sampleLength / 4;
    }

    private static IEnumerable<string> SplitTtmlBlocks(string text)
    {
        foreach (Match match in TtmlBlockRegex().Matches(text))
        {
            yield return match.Value;
        }
    }

    private static string NormalizeLyricText(string value)
    {
        var decoded = WebUtility.HtmlDecode(value);
        var compact = WhitespaceRegex().Replace(decoded, " ").Trim();
        return compact;
    }

    public static bool TryParseTime(string? value, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.EndsWith('s'))
        {
            value = value[..^1];
        }

        var parts = value.Split(':');
        if (parts.Length == 1 && double.TryParse(parts[0], out var secondsOnly))
        {
            time = TimeSpan.FromSeconds(secondsOnly);
            return true;
        }

        if (parts.Length == 2
            && double.TryParse(parts[0], out var minutes)
            && double.TryParse(parts[1], out var seconds))
        {
            time = TimeSpan.FromSeconds(minutes * 60 + seconds);
            return true;
        }

        if (parts.Length == 3
            && double.TryParse(parts[0], out var hours)
            && double.TryParse(parts[1], out minutes)
            && double.TryParse(parts[2], out seconds))
        {
            time = TimeSpan.FromSeconds(hours * 3600 + minutes * 60 + seconds);
            return true;
        }

        return false;
    }

    [GeneratedRegex("<tt[\\s\\S]*?</tt>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TtmlBlockRegex();

    [GeneratedRegex("\\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();
}
