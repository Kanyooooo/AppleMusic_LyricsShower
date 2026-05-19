using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Diagnostics;
using AppleMusicTranslator.Models;

namespace AppleMusicTranslator.Services;

public sealed partial class AppleMusicLyricAnchorService
{
    private static readonly string[] CommonUiText =
    [
        "apple music",
        "play",
        "pause",
        "shuffle",
        "repeat",
        "lyrics",
        "lossless",
        "now playing",
        "search",
        "library",
        "listen now",
        "browse",
        "radio",
        "close",
        "minimize",
        "maximize"
    ];

    public IReadOnlyList<string> FindVisibleAnchors(int processId, TrackInfo track)
    {
        try
        {
            var anchors = new List<VisibleAnchor>();
            foreach (var window in FindAppleMusicWindows(processId))
            {
                CollectText(window, track, anchors);
            }

            return anchors
                .OrderBy(anchor => anchor.Top)
                .ThenBy(anchor => anchor.Left)
                .Select(anchor => anchor.Text)
                .Distinct(StringComparer.Ordinal)
                .Take(80)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<ulong> FindVisibleLyricAddresses(int processId, TrackInfo track)
    {
        try
        {
            var addresses = new List<ulong>();
            foreach (var window in FindAppleMusicWindows(processId))
            {
                CollectVisibleLyricAddresses(window, track, addresses);
            }

            return addresses
                .Distinct()
                .Take(32)
                .ToArray();
        }
        catch
        {
            return Array.Empty<ulong>();
        }
    }

    public bool TryOpenLyricsPanel(int processId)
    {
        try
        {
            foreach (var window in FindAppleMusicWindows(processId))
            {
                if (TryOpenLyricsPanel(window))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static IReadOnlyList<AutomationElement> FindAppleMusicWindows(int processId)
    {
        var windows = new List<AutomationElement>();
        var seenHandles = new HashSet<int>();

        try
        {
            var rootMatches = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId));

            foreach (AutomationElement window in rootMatches)
            {
                AddWindow(window, windows, seenHandles);
            }
        }
        catch
        {
            // Some Apple Music builds do not expose the top window from RootElement by process id.
        }

        try
        {
            var hwnd = Process.GetProcessById(processId).MainWindowHandle;
            if (hwnd != IntPtr.Zero)
            {
                AddWindow(AutomationElement.FromHandle(hwnd), windows, seenHandles);
            }
        }
        catch
        {
            // Best-effort fallback; a missing UIA root should not break memory scanning.
        }

        return windows;
    }

    private static void AddWindow(AutomationElement? window, List<AutomationElement> windows, HashSet<int> seenHandles)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            var handle = window.Current.NativeWindowHandle;
            if (handle == 0 || seenHandles.Add(handle))
            {
                windows.Add(window);
            }
        }
        catch
        {
            windows.Add(window);
        }
    }

    private static void CollectText(AutomationElement root, TrackInfo track, List<VisibleAnchor> anchors)
    {
        Rect windowRect;
        try
        {
            windowRect = root.Current.BoundingRectangle;
        }
        catch
        {
            return;
        }

        if (windowRect.Width <= 0 || windowRect.Height <= 0)
        {
            return;
        }

        AutomationElementCollection elements;
        try
        {
            elements = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        }
        catch
        {
            return;
        }

        foreach (AutomationElement element in elements)
        {
            string? raw;
            try
            {
                raw = element.Current.Name;
            }
            catch
            {
                continue;
            }

            Rect elementRect;
            try
            {
                elementRect = element.Current.BoundingRectangle;
            }
            catch
            {
                continue;
            }

            if (!LooksLikeLyricsPanelText(windowRect, elementRect))
            {
                continue;
            }

            var text = NormalizeVisibleText(raw);
            if (LooksLikeLyricAnchor(text, track))
            {
                anchors.Add(new VisibleAnchor(text, elementRect.Top, elementRect.Left));
            }
        }
    }

    private static void CollectVisibleLyricAddresses(AutomationElement root, TrackInfo track, List<ulong> addresses)
    {
        AutomationElementCollection elements;
        try
        {
            elements = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        }
        catch
        {
            return;
        }

        foreach (AutomationElement element in elements)
        {
            string? raw;
            try
            {
                raw = element.Current.Name;
            }
            catch
            {
                continue;
            }

            var text = NormalizeVisibleText(raw);
            if (!LooksLikeLyricAnchor(text, track))
            {
                continue;
            }

            try
            {
                if (element.Current.NativeWindowHandle != 0)
                {
                    addresses.Add((ulong)element.Current.NativeWindowHandle);
                }
            }
            catch
            {
                // Best-effort only.
            }
        }
    }

    private static bool TryOpenLyricsPanel(AutomationElement root)
    {
        AutomationElementCollection elements;
        try
        {
            elements = root.FindAll(
                TreeScope.Descendants,
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)));
        }
        catch
        {
            return false;
        }

        foreach (AutomationElement element in elements)
        {
            if (!LooksLikeLyricsButton(element))
            {
                continue;
            }

            if (TryTurnOnToggle(element) || TryInvoke(element))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeLyricsButton(AutomationElement element)
    {
        var name = SafeCurrentString(element, AutomationElement.NameProperty);
        var automationId = SafeCurrentString(element, AutomationElement.AutomationIdProperty);
        var helpText = SafeCurrentString(element, AutomationElement.HelpTextProperty);
        var combined = $"{name} {automationId} {helpText}".ToLowerInvariant();

        return combined.Contains("lyrics", StringComparison.Ordinal)
            || combined.Contains("lyric", StringComparison.Ordinal)
            || combined.Contains("歌詞", StringComparison.Ordinal)
            || combined.Contains("歌词", StringComparison.Ordinal)
            || combined.Contains("karaoke", StringComparison.Ordinal);
    }

    private static bool TryTurnOnToggle(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern)
                || pattern is not TogglePattern toggle)
            {
                return false;
            }

            if (toggle.Current.ToggleState == ToggleState.On)
            {
                return true;
            }

            toggle.Toggle();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryInvoke(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                || pattern is not InvokePattern invoke)
            {
                return false;
            }

            invoke.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeCurrentString(AutomationElement element, AutomationProperty property)
    {
        try
        {
            return element.GetCurrentPropertyValue(property) as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool LooksLikeLyricsPanelText(Rect windowRect, Rect elementRect)
    {
        if (elementRect.Width <= 0 || elementRect.Height <= 0)
        {
            return false;
        }

        var rightPanelStart = windowRect.Left + windowRect.Width * 0.54;
        var topChromeEnd = windowRect.Top + Math.Min(96, windowRect.Height * 0.18);
        return elementRect.Left >= rightPanelStart
            && elementRect.Top >= topChromeEnd
            && elementRect.Right <= windowRect.Right + 8
            && elementRect.Bottom <= windowRect.Bottom + 8;
    }

    private static string NormalizeVisibleText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return WhitespaceRegex().Replace(value, " ").Trim();
    }

    private static bool LooksLikeLyricAnchor(string text, TrackInfo track)
    {
        if (text.Length < 4 || text.Length > 80)
        {
            return false;
        }

        if (IsSameText(text, track.Title)
            || IsSameText(text, track.Artist)
            || IsSameText(text, track.Album))
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        if (CommonUiText.Any(ui => lower.Contains(ui, StringComparison.Ordinal)))
        {
            return false;
        }

        if (PlaylistIndexRegex().IsMatch(text))
        {
            return false;
        }

        var letterCount = text.Count(char.IsLetter);
        if (letterCount < 3)
        {
            return false;
        }

        var hasCjkOrKana = text.Any(ch =>
            (ch >= '\u3040' && ch <= '\u30ff')
            || (ch >= '\u3400' && ch <= '\u9fff'));
        var hasPunctuationOrSpace = text.Any(ch => char.IsWhiteSpace(ch) || char.IsPunctuation(ch));

        return hasCjkOrKana || hasPunctuationOrSpace || text.Length >= 8;
    }

    private static bool IsSameText(string left, string right) =>
        !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private sealed record VisibleAnchor(string Text, double Top, double Left);

    [GeneratedRegex("\\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("^\\d{1,3}$|^\\d{1,2}:\\d{2}$", RegexOptions.Compiled)]
    private static partial Regex PlaylistIndexRegex();
}
