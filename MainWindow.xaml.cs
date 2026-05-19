using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.IO;
using System.Reflection;
using AppleMusicTranslator.Models;
using AppleMusicTranslator.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace AppleMusicTranslator;

public partial class MainWindow : Window
{
    private readonly MediaSessionService _mediaSession = new();
    private readonly AppleMusicProcessFinder _processFinder = new();
    private readonly ProcessMemoryTtmlExtractor _memoryExtractor = new();
    private readonly TtmlLyricsParser _ttmlParser = new();
    private readonly LyricsMatcher _lyricsMatcher = new();
    private readonly AppleMusicLyricAnchorService _anchorService = new();
    private readonly LyricsTranslationService _translationService = new();
    private readonly LrcLibLyricsService _fallbackLyrics = new();
    private readonly LyricsCacheService _lyricsCache = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly DispatcherTimer _timer;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ContextMenuStrip _trayMenu = new();
    private readonly DisplayModeCoordinator _displayMode;

    private readonly Dictionary<string, string> _translations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _translationInFlight = new(StringComparer.Ordinal);
    private readonly object _translationLock = new();

    private const double MinimumDisplayConfidence = 0.35;
    private const double MinimumReplaceCachedConfidence = 0.80;
    private const double VisibleAnchorsFallbackConfidence = 0.42;
    private const double LrcLibSyncedFallbackConfidence = 0.68;
    private const double LrcLibPlainFallbackConfidence = 0.36;

    private AppSettings _settings;
    private UiText _ui = UiText.Chinese;
    private TrackInfo _latestTrack = TrackInfo.Empty;
    private string _loadedTrackKey = string.Empty;
    private LyricsBundle _lyrics = LyricsBundle.Empty("No lyrics");
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _translationCancellation;
    private SettingsWindow? _settingsWindow;
    private bool _allowClose;
    private bool _tickRunning;
    private string _lastDisplayedLine = string.Empty;
    private DateTime _lastFullScan = DateTime.MinValue;
    private CancellationTokenSource? _autoRetryCancellation;
    private DateTime _lastVisibleFallbackRefresh = DateTime.MinValue;
    private string _lastVisibleFallbackSnapshot = string.Empty;
    private string _lastLyricsPanelRequestTrackKey = string.Empty;
    private DateTime _lastLyricsPanelRequestUtc = DateTime.MinValue;
    private bool _usingCachedLyrics;
    private double _loadedLyricsConfidence;
    private bool _hasLyricsCacheConflict;
    private string _lyricsCacheConflictDescription = string.Empty;
    private bool _draggingLyrics;
    private WpfPoint _lyricDragStart;
    private double _lyricDragStartX;
    private double _lyricDragStartY;
    private bool _suppressWindowPlacementSave;
    private DateTime _lastPlacementSaveUtc = DateTime.MinValue;
    private ScrollViewerOffsetAnimator? _verticalScrollAnimator;

    public MainWindow()
    {
        InitializeComponent();
        _verticalScrollAnimator = new ScrollViewerOffsetAnimator(VerticalLyricsView);

        _settings = _settingsService.Load();
        _ui = UiText.For(_settings.InterfaceLanguage);
        _displayMode = new DisplayModeCoordinator(
            this,
            _settings,
            () => _ui,
            BuildCurrentDisplayPayload,
            ApplyNormalPlacement,
            RememberWindowPlacement,
            InvalidateCurrentDisplay);
        ApplyLocalizedText();
        RestoreWindowPlacement();
        ApplySettings();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        _timer.Tick += Timer_Tick;

        _trayIcon = CreateTrayIcon();
        _trayIcon.Visible = true;
    }

    private TimeSpan LyricOffset => TimeSpan.FromMilliseconds(_settings.LyricOffsetMs);

    private WindowShellMode CurrentShellMode => _displayMode.CurrentMode;

    internal WindowShellMode CurrentWindowShellMode => _displayMode.CurrentMode;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _displayMode.ApplyHostMode();
        _displayMode.EnforceTopmost();
        _timer.Start();
        await RefreshMediaStateSafelyAsync();
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_tickRunning)
        {
            return;
        }

        _tickRunning = true;
        try
        {
            _displayMode.EnforceTopmost();
            await RefreshMediaStateSafelyAsync();
        }
        finally
        {
            _tickRunning = false;
        }
    }

    private async Task RefreshMediaStateSafelyAsync()
    {
        try
        {
            await RefreshMediaStateAsync();
        }
        catch
        {
            if (!_latestTrack.HasSongIdentity)
            {
                ClearLyrics(_ui.WaitingForAppleMusicTitle);
            }
            else if (_lyrics.Lines.Count == 0)
            {
                SetLyricText(string.Empty, _ui.LyricLoadingFailed);
            }
        }
    }

    private async Task RefreshMediaStateAsync()
    {
        var track = await _mediaSession.GetCurrentTrackAsync();
        _latestTrack = track;

        UpdateTrackUi(track);

        if (!track.HasSongIdentity)
        {
            ClearLyrics(_ui.WaitingForAppleMusicTitle);
            return;
        }

        if (track.CacheKey != _loadedTrackKey)
        {
            BeginLoadLyrics(track);
        }
        else if (_lyrics.Lines.Count > 0)
        {
            if (IsVisibleLyricsFallback(_lyrics))
            {
                await RefreshVisibleLyricsFallbackAsync(track);
            }

            UpdateCurrentLyric(track);
        }
    }

    private void BeginLoadLyrics(TrackInfo track, bool forceReload = false, bool allowAutoRetry = true)
    {
        _autoRetryCancellation?.Cancel();
        _loadCancellation?.Cancel();
        _translationCancellation?.Cancel();
        _loadCancellation = new CancellationTokenSource();

        lock (_translationLock)
        {
            _translations.Clear();
            _translationInFlight.Clear();
        }

        _loadedTrackKey = track.CacheKey;
        _lyrics = LyricsBundle.Empty("Loading");
        _lastDisplayedLine = string.Empty;
        _lastVisibleFallbackRefresh = DateTime.MinValue;
        _lastVisibleFallbackSnapshot = string.Empty;
        _lastLyricsPanelRequestTrackKey = string.Empty;
        _lastLyricsPanelRequestUtc = DateTime.MinValue;
        _usingCachedLyrics = false;
        _loadedLyricsConfidence = 0;
        SetLyricsCacheConflict(false, string.Empty);
        VerticalLyricsPanel.Children.Clear();
        ApplyLyricsViewVisibility();
        _displayMode.NormalizeSettings();

        var hasCachedLyrics = TryApplyCachedLyrics(track);
        if (!hasCachedLyrics)
        {
            SetLyricText(string.Empty, _ui.LoadingLyrics, animate: false);
            UpdateChildWindowDisplay(BuildMessageDisplayPayload(_ui.LoadingLyrics));
            StatusText.Text = _ui.ScanningMemory;
            SourceText.Text = _ui.AppleMusicMemorySource;
        }

        _ = LoadLyricsAsync(track, _loadCancellation.Token, forceReload, allowAutoRetry, hasCachedLyrics);
    }

    private async Task LoadLyricsAsync(
        TrackInfo track,
        CancellationToken cancellationToken,
        bool forceReload,
        bool allowAutoRetry,
        bool hasCachedLyrics)
    {
        try
        {
            var resolution = await ResolveLyricsAsync(track, forceReload, cancellationToken);
            if (resolution.HasLyrics && ShouldApplyResolution(resolution, hasCachedLyrics))
            {
                await ApplyLoadedLyricsAsync(track, resolution);
                StartBackgroundTranslation(track, resolution.Lyrics);
                return;
            }

            if (hasCachedLyrics)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (track.CacheKey == _loadedTrackKey)
                    {
                        StatusText.Text = _ui.CachedLyricsLiveMissed;
                    }
                });
                return;
            }

            if (allowAutoRetry && ScheduleAutoRetry(track, cancellationToken))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    SetLyricText(string.Empty, _ui.LoadingLyrics);
                    StatusText.Text = _ui.ScanningMemory;
                    SourceText.Text = _ui.AppleMusicMemorySource;
                });
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                SetLyricText(string.Empty, _ui.NoLyricsFound);
                StatusText.Text = _ui.OpenLyricsPanelThenRescan;
            });
        }
        catch (OperationCanceledException)
        {
            // Track changed or the user rescanned.
        }
        catch (Exception ex)
        {
            if (hasCachedLyrics)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (track.CacheKey == _loadedTrackKey)
                    {
                        StatusText.Text = _ui.CachedLyricsLiveMissed;
                    }
                });
                return;
            }

            if (allowAutoRetry && ScheduleAutoRetry(track, cancellationToken))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    SetLyricText(string.Empty, _ui.LoadingLyrics);
                    StatusText.Text = _ui.ScanningMemory;
                    SourceText.Text = _ui.AppleMusicMemorySource;
                });
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                SetLyricText(string.Empty, _ui.LyricLoadingFailed);
                StatusText.Text = ex.Message;
            });
        }
    }

    private bool TryApplyCachedLyrics(TrackInfo track)
    {
        if (!_lyricsCache.TryGet(track, out var result) || result.Lyrics.Lines.Count == 0)
        {
            return false;
        }

        _lyrics = result.Lyrics;
        _usingCachedLyrics = true;
        _loadedLyricsConfidence = result.Confidence;
        _lastDisplayedLine = string.Empty;
        SourceText.Text = _ui.SourceFor(result.Lyrics.Source);
        StatusText.Text = WithConfidence(_ui.LoadedCachedLyrics(result.Lyrics.Lines.Count), result.Confidence);
        LoadCachedTranslations(result.Lyrics);
        SetLyricsCacheConflict(result.HasConflict, result.ConflictDescription);
        UpdateCurrentLyric(_latestTrack);
        StartBackgroundTranslation(track, result.Lyrics);
        return true;
    }

    private bool ShouldApplyResolution(LyricsResolution resolution, bool hasCachedLyrics)
    {
        if (!resolution.HasLyrics || resolution.Confidence < MinimumDisplayConfidence)
        {
            return false;
        }

        return !hasCachedLyrics
            || (resolution.Confidence >= MinimumReplaceCachedConfidence
                && resolution.Confidence + 0.001 >= _loadedLyricsConfidence);
    }

    private bool ScheduleAutoRetry(TrackInfo track, CancellationToken loadCancellationToken)
    {
        if (loadCancellationToken.IsCancellationRequested
            || !track.HasSongIdentity
            || !string.Equals(track.CacheKey, _loadedTrackKey, StringComparison.Ordinal))
        {
            return false;
        }

        _autoRetryCancellation?.Cancel();
        _autoRetryCancellation = CancellationTokenSource.CreateLinkedTokenSource(loadCancellationToken);
        var retryToken = _autoRetryCancellation.Token;

        _ = AutoRetryLoadLyricsAsync(track, retryToken);
        return true;
    }

    private async Task AutoRetryLoadLyricsAsync(TrackInfo track, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested
                    || !string.Equals(track.CacheKey, _loadedTrackKey, StringComparison.Ordinal)
                    || !string.Equals(track.CacheKey, _latestTrack.CacheKey, StringComparison.Ordinal))
                {
                    return;
                }

                BeginLoadLyrics(track, forceReload: true, allowAutoRetry: false);
            });
        }
        catch (OperationCanceledException)
        {
            // Track changed or the user rescanned before the automatic retry fired.
        }
    }

    private Task ApplyLoadedLyricsAsync(TrackInfo track, LyricsResolution resolution, bool clearTranslations = true)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            if (track.CacheKey != _loadedTrackKey)
            {
                return;
            }

            var lyrics = resolution.Lyrics;
            _lyrics = lyrics;
            _usingCachedLyrics = LyricsCacheService.IsCachedSource(lyrics.Source);
            _loadedLyricsConfidence = resolution.Confidence;
            _lastDisplayedLine = string.Empty;
            SourceText.Text = _ui.SourceFor(lyrics.Source);
            StatusText.Text = WithConfidence(_ui.LoadedLyricLines(lyrics.Lines.Count), resolution.Confidence);
            LoadCachedTranslations(lyrics, clearTranslations);
            if (!_usingCachedLyrics)
            {
                _lyricsCache.Save(track, lyrics, resolution.Confidence);
            }

            SetLyricsCacheConflict(
                _lyricsCache.HasConflict(track, lyrics, out var conflictDescription),
                conflictDescription);
            UpdateCurrentLyric(_latestTrack);
        }).Task;
    }

    private static string WithConfidence(string text, double confidence) =>
        $"{text} | confidence {Math.Clamp(confidence, 0, 1) * 100:0}%";

    private async Task RequestAppleMusicLyricsPanelAsync(
        int processId,
        TrackInfo track,
        CancellationToken cancellationToken,
        bool force = false)
    {
        if (!_settings.AutoOpenAppleMusicLyricsPanel)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force
            && string.Equals(_lastLyricsPanelRequestTrackKey, track.CacheKey, StringComparison.Ordinal)
            && now - _lastLyricsPanelRequestUtc < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastLyricsPanelRequestTrackKey = track.CacheKey;
        _lastLyricsPanelRequestUtc = now;

        await Task.Run(() => _anchorService.TryOpenLyricsPanel(processId), cancellationToken);
    }

    private async Task<LyricsResolution> ResolveLyricsAsync(TrackInfo track, bool forceReload, CancellationToken cancellationToken)
    {
        var memoryResolution = await FindMemoryLyricsAsync(track, forceReload, cancellationToken);
        if (memoryResolution.HasLyrics
            && memoryResolution.Layer != LyricsResolutionLayer.VisibleAnchors
            && memoryResolution.Confidence >= MinimumReplaceCachedConfidence)
        {
            return memoryResolution;
        }

        if (!memoryResolution.HasLyrics)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                StatusText.Text = _ui.MemoryLyricsMissedFallback;
                SourceText.Text = _ui.LrcLibFallbackSource;
            });
        }

        var fallback = await _fallbackLyrics.SearchAsync(track, cancellationToken);
        var fallbackResolution = BuildLrcLibResolution(fallback, track);
        return BetterResolution(memoryResolution, fallbackResolution);
    }

    private async Task<LyricsResolution> FindMemoryLyricsAsync(TrackInfo track, bool forceReload, CancellationToken cancellationToken)
    {
        var processId = _processFinder.FindProcessId();
        if (processId is null)
        {
            return LyricsResolution.Empty(LyricsResolutionLayer.FullMemoryScan, _ui.AppleMusicProcessNotFound);
        }

        await RequestAppleMusicLyricsPanelAsync(processId.Value, track, cancellationToken);

        var visibleAnchors = await Task.Run(() => _anchorService.FindVisibleAnchors(processId.Value, track), cancellationToken);
        if (visibleAnchors.Count == 0)
        {
            await RequestAppleMusicLyricsPanelAsync(processId.Value, track, cancellationToken, force: true);
            await Task.Delay(TimeSpan.FromMilliseconds(180), cancellationToken);
            visibleAnchors = await Task.Run(() => _anchorService.FindVisibleAnchors(processId.Value, track), cancellationToken);
        }

        if (visibleAnchors.Count > 0)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                StatusText.Text = _ui.ReadVisibleAnchors(visibleAnchors.Count);
            });

            var cachedBlocks = await _memoryExtractor.ExtractCachedAsync(processId.Value, track.CacheKey, cancellationToken);
            if (cachedBlocks.Count > 0)
            {
                var cachedCandidates = _ttmlParser.ParseBlocks(cachedBlocks);
                var cachedMatch = _lyricsMatcher.FindBestMatch(cachedCandidates, track, visibleAnchors);
                if (cachedMatch is { AnchorHits: > 0 })
                {
                    _lastFullScan = DateTime.UtcNow;
                    return FromMatch(cachedMatch, LyricsResolutionLayer.NearAddressScan, "cached memory address");
                }
            }
        }

        var fastMatch = await TryFindAnchoredMatchAsync(processId.Value, track, visibleAnchors, cancellationToken);
        if (fastMatch is not null)
        {
            return FromMatch(fastMatch, LyricsResolutionLayer.NearAddressScan, "visible anchor scan");
        }

        var nearAddressMatch = await TryFindNearAddressMatchAsync(processId.Value, track, visibleAnchors, cancellationToken);
        if (nearAddressMatch is not null)
        {
            return FromMatch(nearAddressMatch, LyricsResolutionLayer.NearAddressScan, "visible lyric address scan");
        }

        if (visibleAnchors.Count == 0)
        {
            return LyricsResolution.Empty(LyricsResolutionLayer.VisibleAnchors, _ui.RejectedStaleMemoryLyrics);
        }

        var shouldFullScan = forceReload || DateTime.UtcNow - _lastFullScan > TimeSpan.FromSeconds(12);
        if (!shouldFullScan)
        {
            return BuildVisibleLyricsResolution(track, visibleAnchors);
        }

        await Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = _ui.ScanningMemoryStep(1, 1);
        });

        var rawBlocks = await _memoryExtractor.ExtractAllAsync(processId.Value, track.CacheKey, cancellationToken);
        var candidates = _ttmlParser.ParseBlocks(rawBlocks);
        var best = _lyricsMatcher.FindBestMatch(candidates, track, visibleAnchors);

        if (best is not null)
        {
            _lastFullScan = DateTime.UtcNow;
        }

        if (visibleAnchors.Count > 0 && best is { AnchorHits: 0 })
        {
            return LyricsResolution.Empty(LyricsResolutionLayer.FullMemoryScan, _ui.RejectedStaleMemoryLyrics);
        }

        return best is not null
            ? FromMatch(best, LyricsResolutionLayer.FullMemoryScan, "full memory scan")
            : BuildVisibleLyricsResolution(track, visibleAnchors);
    }

    private async Task<LyricMatch?> TryFindAnchoredMatchAsync(
        int processId,
        TrackInfo track,
        IReadOnlyCollection<string> visibleAnchors,
        CancellationToken cancellationToken)
    {
        if (visibleAnchors.Count == 0)
        {
            return null;
        }

        var rawBlocks = await _memoryExtractor.ExtractLikelyCurrentAsync(processId, track.CacheKey, visibleAnchors, cancellationToken);
        var candidates = _ttmlParser.ParseBlocks(rawBlocks);
        var match = _lyricsMatcher.FindBestMatch(candidates, track, visibleAnchors);
        return match is not null && match.Confidence >= LyricsMatcher.MinimumAnchoredConfidence ? match : null;
    }

    private async Task<LyricMatch?> TryFindNearAddressMatchAsync(
        int processId,
        TrackInfo track,
        IReadOnlyCollection<string> visibleAnchors,
        CancellationToken cancellationToken)
    {
        if (visibleAnchors.Count == 0)
        {
            return null;
        }

        var addresses = await Task.Run(() => _anchorService.FindVisibleLyricAddresses(processId, track), cancellationToken);
        if (addresses.Count == 0)
        {
            return null;
        }

        var rawBlocks = await Task.Run(
            () => _memoryExtractor.ExtractAroundAddresses(processId, track.CacheKey, addresses, cancellationToken),
            cancellationToken);
        var candidates = _ttmlParser.ParseBlocks(rawBlocks);
        var match = _lyricsMatcher.FindBestMatch(candidates, track, visibleAnchors);
        return match is not null && match.Confidence >= LyricsMatcher.MinimumAnchoredConfidence ? match : null;
    }

    private static LyricsResolution FromMatch(LyricMatch match, LyricsResolutionLayer layer, string detail) =>
        new(match.Lyrics, layer, match.Confidence, detail);

    private LyricsResolution BuildLrcLibResolution(LyricsBundle lyrics, TrackInfo track)
    {
        if (lyrics.Lines.Count == 0)
        {
            return LyricsResolution.Empty(LyricsResolutionLayer.LrcLibFallback, lyrics.Source);
        }

        var confidence = string.Equals(lyrics.Source, LyricsBundle.LrcLibSyncedFallbackSource, StringComparison.OrdinalIgnoreCase)
            ? LrcLibSyncedFallbackConfidence
            : LrcLibPlainFallbackConfidence;

        return new LyricsResolution(
            lyrics,
            LyricsResolutionLayer.LrcLibFallback,
            Math.Clamp(confidence, 0, 1),
            lyrics.Source);
    }

    private static LyricsResolution BetterResolution(LyricsResolution first, LyricsResolution second)
    {
        if (!first.HasLyrics)
        {
            return second;
        }

        if (!second.HasLyrics)
        {
            return first;
        }

        return second.Confidence > first.Confidence ? second : first;
    }

    private static LyricsResolution BuildVisibleLyricsResolution(TrackInfo track, IReadOnlyCollection<string> visibleAnchors)
    {
        var lyrics = BuildVisibleLyricsBundle(track, visibleAnchors);
        return lyrics.Lines.Count == 0
            ? LyricsResolution.Empty(LyricsResolutionLayer.VisibleAnchors, "No Apple Music visible lyrics")
            : new LyricsResolution(
                lyrics,
                LyricsResolutionLayer.VisibleAnchors,
                VisibleAnchorsFallbackConfidence,
                $"visible anchors: {visibleAnchors.Count}");
    }

    private static LyricsBundle BuildVisibleLyricsBundle(TrackInfo track, IReadOnlyCollection<string> visibleAnchors)
    {
        var lines = visibleAnchors
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .Select((text, index) => new LyricLine(
                TimeSpan.FromSeconds(index * 4),
                TimeSpan.FromSeconds((index + 1) * 4),
                text))
            .ToArray();

        return lines.Length == 0
            ? LyricsBundle.Empty("No Apple Music visible lyrics")
            : new LyricsBundle(
                LyricsBundle.AppleMusicVisibleLyricsSource,
                lines,
                false,
                track.Duration,
                string.Join('\n', lines.Select(line => line.Text)));
    }

    private static bool IsVisibleLyricsFallback(LyricsBundle lyrics) =>
        string.Equals(lyrics.Source, LyricsBundle.AppleMusicVisibleLyricsSource, StringComparison.OrdinalIgnoreCase);

    private async Task RefreshVisibleLyricsFallbackAsync(TrackInfo track)
    {
        if (DateTime.UtcNow - _lastVisibleFallbackRefresh < TimeSpan.FromMilliseconds(900))
        {
            return;
        }

        _lastVisibleFallbackRefresh = DateTime.UtcNow;
        var processId = _processFinder.FindProcessId();
        if (processId is null)
        {
            return;
        }

        IReadOnlyList<string> visibleAnchors;
        try
        {
            visibleAnchors = await Task.Run(() => _anchorService.FindVisibleAnchors(processId.Value, track));
        }
        catch
        {
            return;
        }

        if (visibleAnchors.Count == 0)
        {
            return;
        }

        var snapshot = string.Join('\n', visibleAnchors.Take(12));
        if (string.Equals(snapshot, _lastVisibleFallbackSnapshot, StringComparison.Ordinal))
        {
            return;
        }

        _lastVisibleFallbackSnapshot = snapshot;
        var resolution = BuildVisibleLyricsResolution(track, visibleAnchors);
        if (!resolution.HasLyrics)
        {
            return;
        }

        await ApplyLoadedLyricsAsync(track, resolution, clearTranslations: false);
        StartBackgroundTranslation(track, resolution.Lyrics);
    }

    private void LoadCachedTranslations(LyricsBundle lyrics, bool clearExisting = true)
    {
        lock (_translationLock)
        {
            if (clearExisting)
            {
                _translations.Clear();
            }

            foreach (var line in lyrics.Lines)
            {
                if (_translationService.TryGetCachedTranslation(line.Text, _settings.InterfaceLanguage, out var cached))
                {
                    _translations[TranslationRuntimeKey(line.Text, _settings.InterfaceLanguage)] = cached;
                }
            }
        }
    }

    private void SetLyricsCacheConflict(bool hasConflict, string conflictDescription)
    {
        _hasLyricsCacheConflict = hasConflict;
        _lyricsCacheConflictDescription = conflictDescription;
        LyricsConflictBadge.Visibility = hasConflict ? Visibility.Visible : Visibility.Collapsed;
        WrongLyricsButton.ToolTip = hasConflict && !string.IsNullOrWhiteSpace(conflictDescription)
            ? _ui.LyricsConflictTooltip(conflictDescription)
            : _ui.WrongLyricsTooltip;
    }

    private void StartBackgroundTranslation(TrackInfo track, LyricsBundle lyrics)
    {
        _translationCancellation?.Cancel();

        if (!_settings.ShowTranslation || lyrics.Lines.Count == 0)
        {
            return;
        }

        _translationCancellation = new CancellationTokenSource();
        var token = _translationCancellation.Token;
        var trackKey = track.CacheKey;
        var startPosition = _latestTrack.CacheKey == trackKey ? _latestTrack.Position : TimeSpan.Zero;

        var nearbyTexts = lyrics.Lines
            .OrderBy(line => Math.Abs((line.Begin - startPosition).TotalMilliseconds))
            .Select(line => line.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        _ = TranslateTextsInBackgroundAsync(trackKey, nearbyTexts, _settings.InterfaceLanguage, token);
    }

    private async Task TranslateTextsInBackgroundAsync(
        string trackKey,
        IReadOnlyList<string> texts,
        UiLanguage targetLanguage,
        CancellationToken cancellationToken)
    {
        var completed = 0;

        try
        {
            await Parallel.ForEachAsync(
                texts,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 1,
                    CancellationToken = cancellationToken
                },
                async (text, token) =>
                {
                    await TranslateAndStoreAsync(trackKey, text, targetLanguage, token);
                    var done = Interlocked.Increment(ref completed);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_loadedTrackKey == trackKey && _settings.ShowTranslation)
                        {
                            StatusText.Text = _ui.TranslatingInBackground(done, texts.Count);
                        }
                    });
                });
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _translationService.FlushCache();
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (_loadedTrackKey == trackKey)
            {
                StatusText.Text = _ui.ReadyTranslatedLines(texts.Count);
                UpdateCurrentLyric(_latestTrack);
            }
        });
    }

    private async Task TranslateAndStoreAsync(
        string trackKey,
        string text,
        UiLanguage targetLanguage,
        CancellationToken cancellationToken)
    {
        var translationKey = TranslationRuntimeKey(text, targetLanguage);
        lock (_translationLock)
        {
            if (_translations.ContainsKey(translationKey) || !_translationInFlight.Add(translationKey))
            {
                return;
            }
        }

        try
        {
            var translated = await _translationService.TranslateLineAsync(text, targetLanguage, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(translated))
            {
                return;
            }

            lock (_translationLock)
            {
                if (_settings.InterfaceLanguage == targetLanguage)
                {
                    _translations[translationKey] = translated;
                }
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (_loadedTrackKey == trackKey && _settings.InterfaceLanguage == targetLanguage)
                {
                    UpdateCurrentLyric(_latestTrack, force: true);
                }
            });
        }
        finally
        {
            lock (_translationLock)
            {
                _translationInFlight.Remove(translationKey);
            }
        }
    }

    private void RequestCurrentLineTranslation(LyricLine line)
    {
        if (!_settings.ShowTranslation || _translationCancellation is null)
        {
            return;
        }

        _ = TranslateAndStoreAsync(_loadedTrackKey, line.Text, _settings.InterfaceLanguage, _translationCancellation.Token);
    }

    private void UpdateTrackUi(TrackInfo track)
    {
        TrackText.Text = track.HasSongIdentity
            ? _ui.TrackDisplay(track.Title, track.Artist)
            : _ui.WaitingForAppleMusicTitle;

        if (!track.HasSongIdentity)
        {
            StatusText.Text = _ui.WaitingForAppleMusicStatus;
        }

        if (track.Duration > TimeSpan.Zero)
        {
            SongProgress.Maximum = track.Duration.TotalSeconds;
            SongProgress.Value = Math.Clamp(track.Position.TotalSeconds, 0, track.Duration.TotalSeconds);
        }
        else
        {
            SongProgress.Maximum = 1;
            SongProgress.Value = 0;
        }
    }

    private void UpdateCurrentLyric(TrackInfo track, bool force = false)
    {
        if (_lyrics.Lines.Count == 0 || track.CacheKey != _loadedTrackKey)
        {
            return;
        }

        var line = _lyrics.IsSynced
            ? _lyrics.FindLine(track.Position, LyricOffset)
            : _lyrics.Lines.FirstOrDefault();
        var translated = line is null ? string.Empty : GetTranslation(line.Text);
        var hasTranslatedLine = !string.IsNullOrWhiteSpace(translated);
        var displayKey = line is null
            ? string.Empty
            : $"{line.Begin.TotalMilliseconds:0}:{line.Text}:{translated}:{_settings.ShowTranslation}:{_settings.LyricsOnlyMode}:{_settings.LayoutMode}:{_settings.AutoCenterCurrentLyric}:{_settings.AutoScrollLongLyrics}";

        if (!force && displayKey == _lastDisplayedLine)
        {
            return;
        }

        _lastDisplayedLine = displayKey;

        if (line is null)
        {
            SetLyricText(string.Empty, string.Empty);
            return;
        }

        if (CurrentShellMode != WindowShellMode.Normal)
        {
            UpdateChildWindowDisplay(BuildDisplayPayload(line, translated));
            if (_settings.ShowTranslation && string.IsNullOrWhiteSpace(translated))
            {
                RequestCurrentLineTranslation(line);
            }

            return;
        }

        if (_settings.LayoutMode == LyricsLayoutMode.Vertical)
        {
            UpdateVerticalLyrics(line);
            if (_settings.ShowTranslation && string.IsNullOrWhiteSpace(translated))
            {
                RequestCurrentLineTranslation(line);
            }

            return;
        }

        if (!_settings.ShowTranslation)
        {
            SetLyricText(string.Empty, line.Text);
            return;
        }

        if (!hasTranslatedLine)
        {
            SetLyricText(string.Empty, line.Text);
            RequestCurrentLineTranslation(line);
            return;
        }

        SetLyricText(line.Text, translated);
    }

    private void UpdateVerticalLyrics(LyricLine activeLine)
    {
        ApplyLyricsViewVisibility();
        VerticalLyricsPanel.Children.Clear();

        var background = ColorFromHex(_settings.BackgroundColor, WpfColor.FromRgb(24, 27, 34));
        var mainColor = ColorFromHex(_settings.MainColor, Colors.White);
        var originalColor = ColorFromHex(_settings.OriginalColor, WpfColor.FromArgb(0xD5, 0xFF, 0xFF, 0xFF));
        var accentColor = ColorFromHex(_settings.AccentColor, WpfColor.FromRgb(93, 255, 230));
        if (_settings.AutoContrastText)
        {
            mainColor = EnsureReadable(mainColor, background, preferStrong: true);
            originalColor = EnsureReadable(originalColor, background, preferStrong: false);
        }

        var activeIndex = 0;
        for (var index = 0; index < _lyrics.Lines.Count; index++)
        {
            if (ReferenceEquals(_lyrics.Lines[index], activeLine)
                || _lyrics.Lines[index].Begin == activeLine.Begin && _lyrics.Lines[index].Text == activeLine.Text)
            {
                activeIndex = index;
                break;
            }
        }

        for (var index = 0; index < _lyrics.Lines.Count; index++)
        {
            var line = _lyrics.Lines[index];
            var isActive = index == activeIndex;
            var translated = _settings.ShowTranslation ? GetTranslation(line.Text) : string.Empty;
            var primaryText = string.IsNullOrWhiteSpace(translated) ? line.Text : translated;

            var linePanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Vertical
            };

            if (!string.IsNullOrWhiteSpace(translated))
            {
                linePanel.Children.Add(CreateLyricTextBlock(
                    line.Text,
                    originalColor,
                    isActive ? _settings.OriginalFontSize : Math.Max(12, _settings.OriginalFontSize - 2),
                    isActive ? 0.82 : 0.38,
                    FontWeights.Normal));
            }

            linePanel.Children.Add(CreateLyricTextBlock(
                primaryText,
                isActive ? mainColor : originalColor,
                isActive ? _settings.MainFontSize : Math.Max(14, _settings.OriginalFontSize + 1),
                isActive ? 1.0 : 0.42,
                isActive ? FontWeights.Bold : FontWeights.SemiBold));

            var border = new Border
            {
                Margin = new Thickness(0, isActive ? 8 : 4, 0, isActive ? 8 : 4),
                Padding = new Thickness(isActive ? 14 : 8, isActive ? 10 : 6, isActive ? 14 : 8, isActive ? 10 : 6),
                BorderThickness = isActive ? new Thickness(0, 0, 0, 2) : new Thickness(0),
                BorderBrush = new SolidColorBrush(accentColor),
                Opacity = isActive ? 1 : 0.72,
                Child = linePanel
            };

            VerticalLyricsPanel.Children.Add(border);
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!_settings.AutoScrollLongLyrics)
            {
                return;
            }

            VerticalLyricsView.UpdateLayout();
            if (activeIndex >= VerticalLyricsPanel.Children.Count
                || VerticalLyricsPanel.Children[activeIndex] is not FrameworkElement activeElement)
            {
                return;
            }

            var point = activeElement.TranslatePoint(new WpfPoint(0, 0), VerticalLyricsPanel);
            var anchor = _settings.AutoCenterCurrentLyric ? 0.5 : 0.35;
            var target = Math.Max(0, point.Y - Math.Max(40, VerticalLyricsView.ViewportHeight * anchor));
            AnimateVerticalScroll(target);
        }, DispatcherPriority.Background);
    }

    private static TextBlock CreateLyricTextBlock(
        string text,
        WpfColor color,
        double fontSize,
        double opacity,
        FontWeight weight)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(color),
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = fontSize,
            FontWeight = weight,
            Opacity = opacity,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2)
        };
    }

    private void AnimateVerticalScroll(double target)
    {
        _verticalScrollAnimator ??= new ScrollViewerOffsetAnimator(VerticalLyricsView);
        var maximum = Math.Max(0, VerticalLyricsView.ExtentHeight - VerticalLyricsView.ViewportHeight);
        _verticalScrollAnimator.Offset = VerticalLyricsView.VerticalOffset;
        _verticalScrollAnimator.BeginAnimation(
            ScrollViewerOffsetAnimator.OffsetProperty,
            new DoubleAnimation
            {
                To = Math.Clamp(target, 0, maximum),
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private string GetTranslation(string text)
    {
        var translationKey = TranslationRuntimeKey(text, _settings.InterfaceLanguage);
        lock (_translationLock)
        {
            return _translations.TryGetValue(translationKey, out var translation) ? translation : string.Empty;
        }
    }

    private static string TranslationRuntimeKey(string text, UiLanguage targetLanguage) =>
        LyricsTranslationService.CreateCacheKey(text, targetLanguage);

    private void ClearLyrics(string message)
    {
        _autoRetryCancellation?.Cancel();
        _loadCancellation?.Cancel();
        _translationCancellation?.Cancel();
        _loadedTrackKey = string.Empty;
        _lyrics = LyricsBundle.Empty("No lyrics");
        _lastDisplayedLine = string.Empty;
        _lastVisibleFallbackRefresh = DateTime.MinValue;
        _lastVisibleFallbackSnapshot = string.Empty;
        _lastLyricsPanelRequestTrackKey = string.Empty;
        _lastLyricsPanelRequestUtc = DateTime.MinValue;
        _usingCachedLyrics = false;
        SetLyricsCacheConflict(false, string.Empty);

        lock (_translationLock)
        {
            _translations.Clear();
            _translationInFlight.Clear();
        }

        SetLyricText(string.Empty, message, animate: false);
        VerticalLyricsPanel.Children.Clear();
        ApplyLyricsViewVisibility();
        UpdateChildWindowDisplay(BuildMessageDisplayPayload(message));
        SourceText.Text = _ui.AppleMusicMemorySource;
        SongProgress.Maximum = 1;
        SongProgress.Value = 0;
    }

    private void RefreshSettingsWindow()
    {
        _settingsWindow?.ReloadFromSettings();
    }

    private void ApplySettings()
    {
        _displayMode.NormalizeSettings();
        _ui = UiText.For(_settings.InterfaceLanguage);
        ApplyLocalizedText();

        var backgroundColor = ColorFromHex(_settings.BackgroundColor, WpfColor.FromRgb(24, 27, 34));
        var mainColor = ColorFromHex(_settings.MainColor, Colors.White);
        var originalColor = ColorFromHex(_settings.OriginalColor, WpfColor.FromArgb(0xD5, 0xFF, 0xFF, 0xFF));
        if (_settings.AutoContrastText)
        {
            mainColor = EnsureReadable(mainColor, backgroundColor, preferStrong: true);
            originalColor = EnsureReadable(originalColor, backgroundColor, preferStrong: false);
        }

        MainLyricText.FontSize = _settings.MainFontSize;
        MainLyricText.LineHeight = Math.Max(_settings.MainFontSize * 1.24, _settings.MainFontSize + 4);
        OriginalText.FontSize = _settings.OriginalFontSize;
        MainLyricText.Foreground = new SolidColorBrush(mainColor);
        OriginalText.Foreground = new SolidColorBrush(originalColor);

        LyricContentTransform.X = _settings.LyricOffsetX;
        LyricContentTransform.Y = _settings.LyricOffsetY;
        if (_settings.LockLyricsPosition)
        {
            LyricContentTransform.X = 0;
            LyricContentTransform.Y = 0;
        }

        ApplyLyricsViewVisibility();

        HeaderGrid.Visibility = Visibility.Visible;
        FooterGrid.Visibility = Visibility.Visible;
        CornerSettingsButton.Visibility = Visibility.Collapsed;

        Shell.Padding = new Thickness(18);
        Shell.Margin = new Thickness(12);
        Shell.BorderThickness = new Thickness(1);
        Shell.CornerRadius = new CornerRadius(8);
        Shell.Background = new SolidColorBrush(WpfColor.FromArgb(
            (byte)Math.Clamp(_settings.BackgroundOpacity * 255, 0, 255),
            backgroundColor.R,
            backgroundColor.G,
            backgroundColor.B));
        Shell.BorderBrush = BrushFromHex(WithAlpha(_settings.AccentColor, _settings.BorderOpacity), System.Windows.Media.Colors.Cyan);

        LyricGrid.Margin = new Thickness(0, 18, 0, 12);
        _displayMode.ApplyHostMode();
        _displayMode.ApplyChildWindowSettings();
        _displayMode.EnforceTopmost();
        UpdateCurrentLyric(_latestTrack, force: true);
    }

    private void ApplyLocalizedText()
    {
        Title = _ui.WindowTitle;
        TrackText.Text = _latestTrack.HasSongIdentity ? _ui.TrackDisplay(_latestTrack.Title, _latestTrack.Artist) : _ui.WaitingForAppleMusicTitle;
        SettingsButton.Content = _ui.Settings;
        SettingsButton.ToolTip = _ui.SettingsTooltip;
        CornerSettingsButton.ToolTip = _ui.SettingsTooltip;
        WrongLyricsButton.ToolTip = _hasLyricsCacheConflict && !string.IsNullOrWhiteSpace(_lyricsCacheConflictDescription)
            ? _ui.LyricsConflictTooltip(_lyricsCacheConflictDescription)
            : _ui.WrongLyricsTooltip;
        RefreshButton.ToolTip = _ui.RescanLyricsTooltip;
        MinimizeButton.ToolTip = _ui.Minimize;
        CloseButton.ToolTip = _ui.HideWindow;

        ContextSettingsMenuItem.Header = _ui.Settings;
        ContextTranslationMenuItem.Header = _settings.ShowTranslation ? _ui.ToggleTranslationOn : _ui.ToggleTranslationOff;
        ContextLyricsOnlyMenuItem.Header = _ui.LyricsOnlyMode;
        ContextExitMenuItem.Header = _ui.Exit;
        CornerSettingsMenuItem.Header = _ui.Settings;
        CornerRescanMenuItem.Header = _ui.RescanLyrics;
        CornerTranslationMenuItem.Header = _settings.ShowTranslation ? _ui.ToggleTranslationOn : _ui.ToggleTranslationOff;

        SourceText.Text = _lyrics.Lines.Count > 0
            ? _ui.SourceFor(_lyrics.Source)
            : _ui.AppleMusicMemorySource;
        RefreshSettingsWindow();
        UpdateTrayMenuText();
    }

    private void SaveAndApplySettings()
    {
        _settingsService.Save(_settings);
        ApplySettings();
        RefreshSettingsWindow();
    }

    private void ApplyLyricsViewVisibility()
    {
        var isNormal = CurrentShellMode == WindowShellMode.Normal;
        CenterLyricsView.Visibility = isNormal && _settings.LayoutMode == LyricsLayoutMode.Center ? Visibility.Visible : Visibility.Collapsed;
        VerticalLyricsView.Visibility = isNormal && _settings.LayoutMode == LyricsLayoutMode.Vertical ? Visibility.Visible : Visibility.Collapsed;

        if (!isNormal)
        {
            VerticalLyricsPanel.Children.Clear();
        }
    }

    private void RestoreWindowPlacement()
    {
        _displayMode.NormalizeSettings();
        var width = _settings.WindowWidth;
        var height = _settings.WindowHeight;
        var left = _settings.WindowLeft;
        var top = _settings.WindowTop;

        if (!IsFinite(width) || !IsFinite(height))
        {
            return;
        }

        Width = Math.Clamp(width, MinWidth, 2200);
        Height = Math.Clamp(height, MinHeight, 1400);
        if (IsFinite(left) && IsFinite(top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
    }

    private LyricDisplayPayload BuildCurrentDisplayPayload()
    {
        if (_lyrics.Lines.Count == 0 || _latestTrack.CacheKey != _loadedTrackKey)
        {
            var main = string.IsNullOrWhiteSpace(MainLyricText.Text) ? _ui.LoadingLyrics : MainLyricText.Text;
            return BuildMessageDisplayPayload(main);
        }

        var activeLine = _lyrics.IsSynced
            ? _lyrics.FindLine(_latestTrack.Position, LyricOffset)
            : _lyrics.Lines.FirstOrDefault();
        var translated = activeLine is null ? string.Empty : GetTranslation(activeLine.Text);
        return BuildDisplayPayload(activeLine, translated);
    }

    private LyricDisplayPayload BuildDisplayPayload(LyricLine? activeLine, string translated)
    {
        var showTranslatedLine = _settings.ShowTranslation && !string.IsNullOrWhiteSpace(translated) && activeLine is not null;
        return new LyricDisplayPayload(
            showTranslatedLine ? activeLine!.Text : string.Empty,
            showTranslatedLine ? translated : activeLine?.Text ?? string.Empty,
            _lyrics.Lines,
            SnapshotTranslations(),
            activeLine,
            _settings.ShowTranslation,
            _displayMode.ChildLayoutMode);
    }

    private LyricDisplayPayload BuildMessageDisplayPayload(string message) =>
        new(
            string.Empty,
            message,
            Array.Empty<LyricLine>(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            null,
            _settings.ShowTranslation,
            _displayMode.ChildLayoutMode);

    private IReadOnlyDictionary<string, string> SnapshotTranslations()
    {
        lock (_translationLock)
        {
            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in _lyrics.Lines)
            {
                var translationKey = TranslationRuntimeKey(line.Text, _settings.InterfaceLanguage);
                if (_translations.TryGetValue(translationKey, out var translation)
                    && !string.IsNullOrWhiteSpace(translation))
                {
                    snapshot[line.Text] = translation;
                }
            }

            return snapshot;
        }
    }

    private void UpdateChildWindowDisplay(LyricDisplayPayload payload)
    {
        _displayMode.UpdateActiveChild(payload);
    }

    private void ApplyNormalPlacement()
    {
        _suppressWindowPlacementSave = true;
        try
        {
            StopWindowSizeAnimation();
            SetWindowSizeImmediately(
                Math.Clamp(_settings.WindowWidth, 520, 2200),
                Math.Clamp(_settings.WindowHeight, 180, 1400));
            if (IsFinite(_settings.WindowLeft) && IsFinite(_settings.WindowTop))
            {
                var workArea = SystemParameters.WorkArea;
                Left = Math.Clamp(_settings.WindowLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
                Top = Math.Clamp(_settings.WindowTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
            }
        }
        finally
        {
            _suppressWindowPlacementSave = false;
        }
    }

    private void StopWindowSizeAnimation()
    {
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
    }

    private void SetWindowSizeImmediately(double width, double height)
    {
        Width = width;
        Height = height;
    }

    private void RememberWindowPlacement()
    {
        if (_suppressWindowPlacementSave || CurrentShellMode != WindowShellMode.Normal || WindowState != WindowState.Normal)
        {
            return;
        }

        if (!IsFinite(Left) || !IsFinite(Top) || !IsFinite(Width) || !IsFinite(Height))
        {
            return;
        }

        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Math.Clamp(Width, 520, 2200);
        _settings.WindowHeight = Math.Clamp(Height, 180, 1400);

        _settingsService.Save(_settings);
    }

    private void RememberWindowPlacementThrottled()
    {
        if (DateTime.UtcNow - _lastPlacementSaveUtc < TimeSpan.FromMilliseconds(600))
        {
            return;
        }

        _lastPlacementSaveUtc = DateTime.UtcNow;
        RememberWindowPlacement();
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static SolidColorBrush BrushFromHex(string value, System.Windows.Media.Color fallback)
    {
        try
        {
            return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
        }
        catch
        {
            return new SolidColorBrush(fallback);
        }
    }

    private static WpfColor ColorFromHex(string value, WpfColor fallback)
    {
        try
        {
            return (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(NormalizeHexColor(value, includeAlpha: true));
        }
        catch
        {
            return fallback;
        }
    }

    private static WpfColor EnsureReadable(WpfColor requested, WpfColor background, bool preferStrong)
    {
        var contrast = ContrastRatio(requested, background);
        if (contrast >= (preferStrong ? 4.5 : 3.2))
        {
            return requested;
        }

        var white = WpfColor.FromArgb(requested.A, 255, 255, 255);
        var black = WpfColor.FromArgb(requested.A, 12, 14, 18);
        return ContrastRatio(white, background) >= ContrastRatio(black, background)
            ? white
            : black;
    }

    private static double ContrastRatio(WpfColor foreground, WpfColor background)
    {
        var front = RelativeLuminance(foreground);
        var back = RelativeLuminance(background);
        var lighter = Math.Max(front, back);
        var darker = Math.Min(front, back);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(WpfColor color)
    {
        static double Channel(byte value)
        {
            var scaled = value / 255.0;
            return scaled <= 0.03928
                ? scaled / 12.92
                : Math.Pow((scaled + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R)
            + 0.7152 * Channel(color.G)
            + 0.0722 * Channel(color.B);
    }

    private static string WithAlpha(string value, double opacity)
    {
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(NormalizeHexColor(value, includeAlpha: true));
            color.A = (byte)Math.Clamp(opacity * 255, 0, 255);
            return color.ToString();
        }
        catch
        {
            return "#665DFFE6";
        }
    }

    private static string NormalizeHexColor(string value, bool includeAlpha)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "#FFFFFF" : value.Trim();
        if (!normalized.StartsWith('#'))
        {
            normalized = "#" + normalized;
        }

        if (normalized.Length == 7 && includeAlpha)
        {
            normalized = "#FF" + normalized[1..];
        }

        return normalized;
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.ReloadFromSettings();
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, this)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    internal void ShowWindowAndBringFront()
    {
        _displayMode.ShowAndBringFront();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_latestTrack.HasSongIdentity)
        {
            BeginLoadLyrics(_latestTrack, forceReload: true);
        }
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        if (CurrentShellMode != WindowShellMode.Normal)
        {
            return;
        }

        RememberWindowPlacementThrottled();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CurrentShellMode != WindowShellMode.Normal)
        {
            return;
        }

        RememberWindowPlacementThrottled();
    }

    private void WrongLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_latestTrack.HasSongIdentity)
        {
            return;
        }

        var removed = _lyricsCache.Delete(_latestTrack);
        _usingCachedLyrics = false;
        SetLyricsCacheConflict(false, string.Empty);
        StatusText.Text = removed ? _ui.DeletedCachedLyrics : _ui.NoCachedLyricsToDelete;
        _lyrics = LyricsBundle.Empty("Deleted cached lyrics");
        _lastDisplayedLine = string.Empty;
        VerticalLyricsPanel.Children.Clear();
        SetLyricText(string.Empty, _ui.LoadingLyrics, animate: false);
        BeginLoadLyrics(_latestTrack, forceReload: true);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();

    private void CloseMenuItem_Click(object sender, RoutedEventArgs e) => ExitApplication();

    private void CornerSettingsButton_ContextMenuOpening(object sender, ContextMenuEventArgs e) => ApplyLocalizedText();

    private void CornerRescanMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_latestTrack.HasSongIdentity)
        {
            BeginLoadLyrics(_latestTrack, forceReload: true);
        }
    }

    private void ToggleTranslationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ToggleTranslation();
    }

    private void LyricsOnlyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ActivateWindowShellMode(CurrentShellMode == WindowShellMode.LyricsOnly
            ? WindowShellMode.Normal
            : WindowShellMode.LyricsOnly);
    }

    internal void NotifySettingsChanged(SettingsChangeKind changeKind)
    {
        _displayMode.NormalizeSettings();

        if (changeKind.HasFlag(SettingsChangeKind.Language))
        {
            lock (_translationLock)
            {
                _translations.Clear();
                _translationInFlight.Clear();
            }
        }

        SaveAndApplySettings();
        UpdateTrayMenuText();

        if (changeKind.HasFlag(SettingsChangeKind.Language))
        {
            LoadCachedTranslations(_lyrics);
            OnTranslationSettingChanged();
        }
        else if (changeKind.HasFlag(SettingsChangeKind.Translation))
        {
            OnTranslationSettingChanged();
        }
    }

    internal void SetWindowShellMode(WindowShellMode mode)
    {
        _displayMode.SetMode(mode);
    }

    internal void ActivateWindowShellMode(WindowShellMode mode)
    {
        SetWindowShellMode(mode);
        SaveAndApplySettings();
        if (IsLoaded)
        {
            ShowWindowAndBringFront();
        }

        UpdateTrayMenuText();
    }

    internal void SaveSettingsOnly() => _settingsService.Save(_settings);

    internal void OpenSettingsFromChild() => ShowSettingsWindow();

    internal void RescanLyricsFromChild()
    {
        if (_latestTrack.HasSongIdentity)
        {
            BeginLoadLyrics(_latestTrack, forceReload: true);
        }
    }

    internal void ToggleTranslationFromChild() => ToggleTranslation();

    private void InvalidateCurrentDisplay()
    {
        _lastDisplayedLine = string.Empty;
    }

    private void ToggleTranslation()
    {
        _settings.ShowTranslation = !_settings.ShowTranslation;
        OnTranslationSettingChanged();
        SaveAndApplySettings();
    }

    private void OnTranslationSettingChanged()
    {
        if (_settings.ShowTranslation)
        {
            StartBackgroundTranslation(_latestTrack, _lyrics);
        }
        else
        {
            _translationCancellation?.Cancel();
        }

        UpdateCurrentLyric(_latestTrack, force: true);
    }

    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.LockLyricsPosition && IsWithinElement(e.OriginalSource, LyricGrid))
        {
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            RememberWindowPlacement();
        }
    }

    private static bool IsWithinElement(object source, DependencyObject element)
    {
        if (source is not DependencyObject current)
        {
            return false;
        }

        while (current is not null)
        {
            if (ReferenceEquals(current, element))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void LyricGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.LockLyricsPosition)
        {
            e.Handled = false;
            return;
        }

        _draggingLyrics = true;
        _lyricDragStart = e.GetPosition(this);
        _lyricDragStartX = _settings.LyricOffsetX;
        _lyricDragStartY = _settings.LyricOffsetY;
        LyricGrid.CaptureMouse();
        e.Handled = true;
    }

    private void LyricGrid_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_draggingLyrics)
        {
            return;
        }

        var current = e.GetPosition(this);
        _settings.LyricOffsetX = Math.Clamp(Math.Round(_lyricDragStartX + current.X - _lyricDragStart.X), -360, 360);
        _settings.LyricOffsetY = Math.Clamp(Math.Round(_lyricDragStartY + current.Y - _lyricDragStart.Y), -180, 180);
        LyricContentTransform.X = _settings.LyricOffsetX;
        LyricContentTransform.Y = _settings.LyricOffsetY;
        RefreshSettingsWindow();
        e.Handled = true;
    }

    private void LyricGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingLyrics)
        {
            return;
        }

        _draggingLyrics = false;
        LyricGrid.ReleaseMouseCapture();
        _settingsService.Save(_settings);
        e.Handled = true;
    }

    private void SetLyricText(string original, string main, bool animate = true)
    {
        ApplyLyricTextLayout(original);
        if (!animate || OriginalText.Text == original && MainLyricText.Text == main)
        {
            OriginalText.Text = original;
            MainLyricText.Text = main;
            OriginalText.Opacity = 1;
            MainLyricText.Opacity = 1;
            return;
        }

        AnimateTextChange(OriginalText, original);
        AnimateTextChange(MainLyricText, main);
    }

    private void ApplyLyricTextLayout(string original)
    {
        var hasOriginal = !string.IsNullOrWhiteSpace(original);
        OriginalText.Visibility = hasOriginal ? Visibility.Visible : Visibility.Collapsed;
        MainLyricText.Margin = hasOriginal ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }

    private static void AnimateTextChange(TextBlock textBlock, string nextText)
    {
        if (textBlock.Text == nextText)
        {
            return;
        }

        var transform = textBlock.RenderTransform as TranslateTransform;
        if (transform is null)
        {
            transform = new TranslateTransform();
            textBlock.RenderTransform = transform;
        }

        var storyboard = new Storyboard();
        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(90),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        var slideOut = new DoubleAnimation
        {
            To = -6,
            Duration = TimeSpan.FromMilliseconds(90),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        Storyboard.SetTarget(fadeOut, textBlock);
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(slideOut, transform);
        Storyboard.SetTargetProperty(slideOut, new PropertyPath(TranslateTransform.YProperty));

        storyboard.Children.Add(fadeOut);
        storyboard.Children.Add(slideOut);
        storyboard.Completed += (_, _) =>
        {
            textBlock.Text = nextText;
            transform.Y = 8;

            var fadeIn = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(190),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var slideIn = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(190),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            textBlock.BeginAnimation(OpacityProperty, fadeIn);
            transform.BeginAnimation(TranslateTransform.YProperty, slideIn);
        };

        storyboard.Begin();
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        _trayMenu.Items.Add("show", null, (_, _) => Dispatcher.Invoke(ShowWindowAndBringFront));
        _trayMenu.Items.Add("settings", null, (_, _) => Dispatcher.Invoke(ShowSettingsWindow));
        _trayMenu.Items.Add("translation", null, (_, _) => Dispatcher.Invoke(ToggleTranslation));
        _trayMenu.Items.Add("lyrics-only", null, (_, _) => Dispatcher.Invoke(() =>
        {
            ActivateWindowShellMode(CurrentShellMode == WindowShellMode.LyricsOnly
                ? WindowShellMode.Normal
                : WindowShellMode.LyricsOnly);
        }));
        _trayMenu.Items.Add("island", null, (_, _) => Dispatcher.Invoke(() =>
        {
            ActivateWindowShellMode(CurrentShellMode == WindowShellMode.Island
                ? WindowShellMode.Normal
                : WindowShellMode.Island);
        }));
        _trayMenu.Items.Add("rescan", null, (_, _) => Dispatcher.Invoke(() =>
        {
            if (_latestTrack.HasSongIdentity)
            {
                BeginLoadLyrics(_latestTrack, forceReload: true);
            }
        }));
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("exit", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        var tray = new Forms.NotifyIcon
        {
            Text = _ui.WindowTitle,
            Icon = LoadAppIcon(),
            ContextMenuStrip = _trayMenu
        };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindowAndBringFront);
        UpdateTrayMenuText();
        return tray;
    }

    private static Drawing.Icon LoadAppIcon()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("app-icon.ico", StringComparison.OrdinalIgnoreCase));
            if (resourceName is not null)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is not null)
                {
                    return new Drawing.Icon(stream);
                }
            }
        }
        catch
        {
            // Fallback to the system icon if the embedded icon cannot be loaded.
        }

        return Drawing.SystemIcons.Application;
    }

    private void UpdateTrayMenuText()
    {
        if (_trayMenu.Items.Count < 8)
        {
            return;
        }

        _trayMenu.Items[0].Text = _ui.ShowWindow;
        _trayMenu.Items[1].Text = _ui.Settings;
        _trayMenu.Items[2].Text = _settings.ShowTranslation ? _ui.ToggleTranslationOn : _ui.ToggleTranslationOff;
        _trayMenu.Items[3].Text = CurrentShellMode == WindowShellMode.LyricsOnly ? _ui.ExitLyricsOnly : _ui.LyricsOnlyMode;
        _trayMenu.Items[4].Text = CurrentShellMode == WindowShellMode.Island ? _ui.NormalWindow : _ui.DynamicIsland;
        _trayMenu.Items[5].Text = _ui.RescanLyrics;
        _trayMenu.Items[7].Text = _ui.Exit;

        if (_trayIcon is not null)
        {
            _trayIcon.Text = _ui.WindowTitle;
        }
    }

    private void ExitApplication()
    {
        _allowClose = true;
        _settingsWindow?.Close();
        _displayMode.CloseChildWindows();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (CurrentShellMode == WindowShellMode.Normal)
        {
            Hide();
        }
        else
        {
            ShowWindowAndBringFront();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        RememberWindowPlacement();
        _displayMode.CloseChildWindows();
        _settingsWindow?.Close();
        _autoRetryCancellation?.Cancel();
        _loadCancellation?.Cancel();
        _translationCancellation?.Cancel();
        _translationService.FlushCache();
        _settingsService.Save(_settings);
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnClosed(e);
    }

    private sealed class ScrollViewerOffsetAnimator : Animatable
    {
        private readonly ScrollViewer _scrollViewer;

        public ScrollViewerOffsetAnimator(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;
        }

        public static readonly DependencyProperty OffsetProperty = DependencyProperty.Register(
            nameof(Offset),
            typeof(double),
            typeof(ScrollViewerOffsetAnimator),
            new PropertyMetadata(0.0, OnOffsetChanged));

        public double Offset
        {
            get => (double)GetValue(OffsetProperty);
            set => SetValue(OffsetProperty, value);
        }

        private static void OnOffsetChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            if (dependencyObject is ScrollViewerOffsetAnimator animator)
            {
                animator._scrollViewer.ScrollToVerticalOffset((double)e.NewValue);
            }
        }

        protected override Freezable CreateInstanceCore() =>
            new ScrollViewerOffsetAnimator(_scrollViewer);
    }
}
