using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
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

    private readonly Dictionary<string, string> _translations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _translationInFlight = new(StringComparer.Ordinal);
    private readonly object _translationLock = new();

    private AppSettings _settings;
    private UiText _ui = UiText.Chinese;
    private TrackInfo _latestTrack = TrackInfo.Empty;
    private string _loadedTrackKey = string.Empty;
    private LyricsBundle _lyrics = LyricsBundle.Empty("No lyrics");
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _translationCancellation;
    private bool _applyingSettings;
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
        ApplyLocalizedText();
        ApplySettingsToControls();
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

    private bool IsIslandMode => _settings.LayoutMode == LyricsLayoutMode.Island;

    private bool IsCompactLyricsMode => _settings.LyricsOnlyMode || IsIslandMode;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        EnforceTopmost();
        _timer.Start();
        await RefreshMediaStateAsync();
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
            EnforceTopmost();
            await RefreshMediaStateAsync();
        }
        finally
        {
            _tickRunning = false;
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
        SetLyricsCacheConflict(false, string.Empty);
        VerticalLyricsPanel.Children.Clear();
        ApplyLyricsViewVisibility();

        var hasCachedLyrics = TryApplyCachedLyrics(track);
        if (!hasCachedLyrics)
        {
            SetLyricText(string.Empty, _ui.LoadingLyrics, animate: false);
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
            var fallbackTask = _fallbackLyrics.SearchAsync(track, cancellationToken);
            var memoryTask = FindMemoryLyricsAsync(track, forceReload, cancellationToken);
            var lyrics = await Task.WhenAny(memoryTask, fallbackTask) == fallbackTask
                ? await fallbackTask
                : await memoryTask;

            if (lyrics.Lines.Count > 0)
            {
                await ApplyLoadedLyricsAsync(track, lyrics);
                StartBackgroundTranslation(track, lyrics);
            }

            if (!memoryTask.IsCompleted)
            {
                var memoryLyrics = await memoryTask;
                if (memoryLyrics.Lines.Count > 0)
                {
                    var canReplaceFallback = !lyrics.Source.StartsWith("LRCLIB", StringComparison.OrdinalIgnoreCase)
                        || memoryLyrics.Source.Equals(LyricsBundle.AppleMusicMemoryTtmlSource, StringComparison.OrdinalIgnoreCase);
                    if (canReplaceFallback)
                    {
                        await ApplyLoadedLyricsAsync(track, memoryLyrics);
                        StartBackgroundTranslation(track, memoryLyrics);
                    }

                    return;
                }
            }

            if (lyrics.Lines.Count == 0)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusText.Text = _ui.MemoryLyricsMissedFallback;
                    SourceText.Text = _ui.LrcLibFallbackSource;
                });

                lyrics = await fallbackTask;
                if (lyrics.Lines.Count > 0)
                {
                    await ApplyLoadedLyricsAsync(track, lyrics);
                    StartBackgroundTranslation(track, lyrics);
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
                return;
            }
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
        _lastDisplayedLine = string.Empty;
        SourceText.Text = _ui.SourceFor(result.Lyrics.Source);
        StatusText.Text = _ui.LoadedCachedLyrics(result.Lyrics.Lines.Count);
        LoadCachedTranslations(result.Lyrics);
        SetLyricsCacheConflict(result.HasConflict, result.ConflictDescription);
        UpdateCurrentLyric(_latestTrack);
        StartBackgroundTranslation(track, result.Lyrics);
        return true;
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

    private Task ApplyLoadedLyricsAsync(TrackInfo track, LyricsBundle lyrics, bool clearTranslations = true)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            if (track.CacheKey != _loadedTrackKey)
            {
                return;
            }

            _lyrics = lyrics;
            _usingCachedLyrics = LyricsCacheService.IsCachedSource(lyrics.Source);
            _lastDisplayedLine = string.Empty;
            SourceText.Text = _ui.SourceFor(lyrics.Source);
            StatusText.Text = _ui.LoadedLyricLines(lyrics.Lines.Count);
            LoadCachedTranslations(lyrics, clearTranslations);
            if (!_usingCachedLyrics)
            {
                _lyricsCache.Save(track, lyrics);
            }

            SetLyricsCacheConflict(
                _lyricsCache.HasConflict(track, lyrics, out var conflictDescription),
                conflictDescription);
            UpdateCurrentLyric(_latestTrack);
        }).Task;
    }

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

    private async Task<LyricsBundle> FindMemoryLyricsAsync(TrackInfo track, bool forceReload, CancellationToken cancellationToken)
    {
        var processId = _processFinder.FindProcessId();
        if (processId is null)
        {
            return LyricsBundle.Empty(_ui.AppleMusicProcessNotFound);
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
                    return cachedMatch.Lyrics;
                }
            }
        }

        var fastMatch = await TryFindAnchoredMatchAsync(processId.Value, track, visibleAnchors, cancellationToken);
        if (fastMatch is not null)
        {
            return fastMatch.Lyrics;
        }

        if (visibleAnchors.Count == 0)
        {
            return LyricsBundle.Empty(_ui.RejectedStaleMemoryLyrics);
        }

        var shouldFullScan = forceReload || DateTime.UtcNow - _lastFullScan > TimeSpan.FromSeconds(12);
        if (!shouldFullScan)
        {
            return BuildVisibleLyricsBundle(track, visibleAnchors);
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
            return LyricsBundle.Empty(_ui.RejectedStaleMemoryLyrics);
        }

        return best?.Lyrics ?? BuildVisibleLyricsBundle(track, visibleAnchors);
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
        return match is not null && match.Score >= 1000 ? match : null;
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
        var lyrics = BuildVisibleLyricsBundle(track, visibleAnchors);
        if (lyrics.Lines.Count == 0)
        {
            return;
        }

        await ApplyLoadedLyricsAsync(track, lyrics, clearTranslations: false);
        StartBackgroundTranslation(track, lyrics);
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
                if (_translationService.TryGetCachedTranslation(line.Text, out var cached))
                {
                    _translations[line.Text] = cached;
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

        _ = TranslateTextsInBackgroundAsync(trackKey, nearbyTexts, token);
    }

    private async Task TranslateTextsInBackgroundAsync(string trackKey, IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var completed = 0;

        try
        {
            await Parallel.ForEachAsync(
                texts,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = cancellationToken
                },
                async (text, token) =>
                {
                    await TranslateAndStoreAsync(trackKey, text, token);
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

    private async Task TranslateAndStoreAsync(string trackKey, string text, CancellationToken cancellationToken)
    {
        lock (_translationLock)
        {
            if (_translations.ContainsKey(text) || !_translationInFlight.Add(text))
            {
                return;
            }
        }

        try
        {
            var translated = await _translationService.TranslateLineAsync(text, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_translationLock)
            {
                _translations[text] = translated;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (_loadedTrackKey == trackKey)
                {
                    UpdateCurrentLyric(_latestTrack, force: true);
                }
            });
        }
        finally
        {
            lock (_translationLock)
            {
                _translationInFlight.Remove(text);
            }
        }
    }

    private void RequestCurrentLineTranslation(LyricLine line)
    {
        if (!_settings.ShowTranslation || _translationCancellation is null)
        {
            return;
        }

        _ = TranslateAndStoreAsync(_loadedTrackKey, line.Text, _translationCancellation.Token);
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

        if (_settings.LayoutMode == LyricsLayoutMode.Vertical)
        {
            UpdateVerticalLyrics(line);
            if (_settings.ShowTranslation && string.IsNullOrWhiteSpace(translated))
            {
                RequestCurrentLineTranslation(line);
            }

            return;
        }

        if (_settings.LayoutMode == LyricsLayoutMode.Island)
        {
            if (!_settings.ShowTranslation)
            {
                SetIslandLyricText(string.Empty, line.Text);
                return;
            }

            if (string.IsNullOrWhiteSpace(translated))
            {
                SetIslandLyricText(string.Empty, line.Text);
                RequestCurrentLineTranslation(line);
                return;
            }

            SetIslandLyricText(line.Text, translated);
            return;
        }

        if (!_settings.ShowTranslation)
        {
            SetLyricText(string.Empty, line.Text);
            return;
        }

        if (string.IsNullOrWhiteSpace(translated))
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
        lock (_translationLock)
        {
            return _translations.TryGetValue(text, out var translation) ? translation : string.Empty;
        }
    }

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
        SourceText.Text = _ui.AppleMusicMemorySource;
        SongProgress.Maximum = 1;
        SongProgress.Value = 0;
    }

    private void ApplySettingsToControls()
    {
        _applyingSettings = true;
        try
        {
            ShowTranslationCheckBox.IsChecked = _settings.ShowTranslation;
            LyricsOnlyCheckBox.IsChecked = _settings.LyricsOnlyMode;
            VerticalLyricsCheckBox.IsChecked = _settings.LayoutMode == LyricsLayoutMode.Vertical;
            IslandModeCheckBox.IsChecked = _settings.LayoutMode == LyricsLayoutMode.Island;
            AutoScrollLongLyricsCheckBox.IsChecked = _settings.AutoScrollLongLyrics;
            AutoCenterCurrentLyricCheckBox.IsChecked = _settings.AutoCenterCurrentLyric;
            AutoLyricsPanelCheckBox.IsChecked = _settings.AutoOpenAppleMusicLyricsPanel;
            AutoContrastCheckBox.IsChecked = _settings.AutoContrastText;
            MainFontSlider.Value = _settings.MainFontSize;
            OriginalFontSlider.Value = _settings.OriginalFontSize;
            BackgroundOpacitySlider.Value = _settings.BackgroundOpacity * 100;
            LyricOffsetSlider.Value = _settings.LyricOffsetMs;
            LyricPositionXSlider.Value = _settings.LyricOffsetX;
            LyricPositionYSlider.Value = _settings.LyricOffsetY;
            IslandWidthSlider.Value = _settings.IslandWidth;
            IslandHeightSlider.Value = _settings.IslandHeight;
            IslandTopOffsetSlider.Value = _settings.IslandTopOffset;
            SelectLanguageComboItem();
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void SelectLanguageComboItem()
    {
        var tag = _settings.InterfaceLanguage.ToString();
        foreach (var item in InterfaceLanguageComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                InterfaceLanguageComboBox.SelectedItem = item;
                return;
            }
        }

        InterfaceLanguageComboBox.SelectedIndex = 0;
    }

    private void ApplySettings()
    {
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
        IslandMainLyricText.FontSize = Math.Clamp(_settings.MainFontSize * 0.72, 18, 32);
        IslandOriginalText.FontSize = Math.Clamp(_settings.OriginalFontSize * 0.78, 11, 18);
        MainLyricText.Foreground = new SolidColorBrush(mainColor);
        OriginalText.Foreground = new SolidColorBrush(originalColor);
        IslandMainLyricText.Foreground = new SolidColorBrush(mainColor);
        IslandOriginalText.Foreground = new SolidColorBrush(originalColor);
        IslandAccentBar.Background = new SolidColorBrush(ColorFromHex(_settings.AccentColor, WpfColor.FromRgb(93, 255, 230)));

        LyricContentTransform.X = _settings.LyricOffsetX;
        LyricContentTransform.Y = _settings.LyricOffsetY;
        if (_settings.LockLyricsPosition)
        {
            LyricContentTransform.X = 0;
            LyricContentTransform.Y = 0;
        }

        ApplyLyricsViewVisibility();

        var compactChrome = IsCompactLyricsMode;
        var showChrome = !compactChrome || SettingsPanel.Visibility == Visibility.Visible;
        HeaderGrid.Visibility = showChrome ? Visibility.Visible : Visibility.Collapsed;
        FooterGrid.Visibility = showChrome ? Visibility.Visible : Visibility.Collapsed;
        CornerSettingsButton.Visibility = compactChrome && SettingsPanel.Visibility != Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsPanel.Visibility = compactChrome && !IsActive
            ? Visibility.Collapsed
            : SettingsPanel.Visibility;

        Shell.Padding = IsIslandMode ? new Thickness(16, 10, 18, 10) : _settings.LyricsOnlyMode ? new Thickness(8) : new Thickness(18);
        Shell.Margin = IsIslandMode ? new Thickness(0) : _settings.LyricsOnlyMode ? new Thickness(0) : new Thickness(12);
        Shell.BorderThickness = _settings.LyricsOnlyMode && !IsIslandMode ? new Thickness(0) : new Thickness(1);
        Shell.CornerRadius = IsIslandMode ? new CornerRadius(Math.Max(28, _settings.IslandHeight / 2)) : _settings.LyricsOnlyMode ? new CornerRadius(0) : new CornerRadius(8);
        Shell.Background = _settings.LyricsOnlyMode && !IsIslandMode
            ? WpfBrushes.Transparent
            : new SolidColorBrush(WpfColor.FromArgb(
                (byte)Math.Clamp(_settings.BackgroundOpacity * 255, 0, 255),
                backgroundColor.R,
                backgroundColor.G,
                backgroundColor.B));
        Shell.BorderBrush = BrushFromHex(WithAlpha(_settings.AccentColor, _settings.BorderOpacity), System.Windows.Media.Colors.Cyan);
        SettingsPanel.BorderBrush = Shell.BorderBrush;

        LyricGrid.Margin = IsIslandMode ? new Thickness(0) : _settings.LyricsOnlyMode ? new Thickness(0) : new Thickness(0, 18, 0, 12);
        ApplyWindowModePlacement();
        EnforceTopmost();
        UpdateCurrentLyric(_latestTrack, force: true);
    }

    private void ApplyLocalizedText()
    {
        Title = _ui.WindowTitle;
        TrackText.Text = _latestTrack.HasSongIdentity ? _ui.TrackDisplay(_latestTrack.Title, _latestTrack.Artist) : _ui.WaitingForAppleMusicTitle;
        SettingsButton.Content = _ui.Settings;
        SettingsButton.ToolTip = _ui.SettingsTooltip;
        CornerSettingsButton.ToolTip = _ui.SettingsTooltip;
        SettingsPanelTitle.Text = _ui.DisplaySettings;
        ShowTranslationCheckBox.Content = _ui.ShowTranslation;
        LyricsOnlyCheckBox.Content = _ui.LyricsOnlyWindow;
        VerticalLyricsCheckBox.Content = _ui.VerticalLyrics;
        IslandModeCheckBox.Content = _ui.DynamicIsland;
        AutoScrollLongLyricsCheckBox.Content = _ui.AutoScrollLongLyrics;
        AutoCenterCurrentLyricCheckBox.Content = _ui.AutoCenterCurrentLyric;
        AutoLyricsPanelCheckBox.Content = _ui.AutoOpenLyricsPanel;
        AutoContrastCheckBox.Content = _ui.AutoContrastText;
        InterfaceLanguageLabel.Text = _ui.InterfaceLanguage;
        LyricOffsetLabel.Text = $"{_ui.LyricsOffset}: {_ui.LyricOffsetValue(_settings.LyricOffsetMs)}";
        LyricOffsetHintText.Text = _ui.LyricsOffsetHint;
        MainFontLabel.Text = _ui.MainTextSize;
        OriginalFontLabel.Text = _ui.OriginalTextSize;
        BackgroundOpacityLabel.Text = _ui.BackgroundOpacity;
        LyricPositionXLabel.Text = $"{_ui.LyricPositionX}: {_ui.LyricPositionValue(_settings.LyricOffsetX)}";
        LyricPositionYLabel.Text = $"{_ui.LyricPositionY}: {_ui.LyricPositionValue(_settings.LyricOffsetY)}";
        IslandWidthLabel.Text = $"{_ui.IslandWidth}: {_ui.PixelValue(_settings.IslandWidth)}";
        IslandHeightLabel.Text = $"{_ui.IslandHeight}: {_ui.PixelValue(_settings.IslandHeight)}";
        IslandTopOffsetLabel.Text = $"{_ui.IslandTopOffset}: {_ui.PixelValue(_settings.IslandTopOffset)}";
        MainColorLabel.Text = _ui.MainColor;
        OriginalColorLabel.Text = _ui.OriginalColor;
        BackgroundColorLabel.Text = _ui.BackgroundColor;
        AccentColorLabel.Text = _ui.AccentColor;
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

        foreach (var item in InterfaceLanguageComboBox.Items.OfType<ComboBoxItem>())
        {
            var tag = item.Tag?.ToString();
            item.Content = tag == nameof(UiLanguage.English) ? _ui.LanguageEnglish : _ui.LanguageChinese;
        }

        SourceText.Text = _lyrics.Lines.Count > 0
            ? _ui.SourceFor(_lyrics.Source)
            : _ui.AppleMusicMemorySource;
        UpdateTrayMenuText();
    }

    private void SaveAndApplySettings()
    {
        if (_applyingSettings)
        {
            return;
        }

        _settingsService.Save(_settings);
        ApplySettings();
    }

    private void ApplyLyricsViewVisibility()
    {
        CenterLyricsView.Visibility = _settings.LayoutMode == LyricsLayoutMode.Center ? Visibility.Visible : Visibility.Collapsed;
        VerticalLyricsView.Visibility = _settings.LayoutMode == LyricsLayoutMode.Vertical ? Visibility.Visible : Visibility.Collapsed;
        IslandLyricsView.Visibility = _settings.LayoutMode == LyricsLayoutMode.Island ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestoreWindowPlacement()
    {
        var restoringLyricsOnly = _settings.LyricsOnlyMode
            && IsFinite(_settings.LyricsOnlyWidth)
            && IsFinite(_settings.LyricsOnlyHeight);
        var width = restoringLyricsOnly ? _settings.LyricsOnlyWidth : _settings.WindowWidth;
        var height = restoringLyricsOnly ? _settings.LyricsOnlyHeight : _settings.WindowHeight;
        var left = restoringLyricsOnly ? _settings.LyricsOnlyLeft : _settings.WindowLeft;
        var top = restoringLyricsOnly ? _settings.LyricsOnlyTop : _settings.WindowTop;

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

    private void ApplyWindowModePlacement()
    {
        if (IsIslandMode)
        {
            ApplyIslandPlacement();
            PulseIsland();
            return;
        }

        ResizeMode = _settings.LyricsOnlyMode ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        MinWidth = _settings.LyricsOnlyMode ? 360 : 520;
        MinHeight = _settings.LyricsOnlyMode ? 120 : 180;
        if (_settings.LyricsOnlyMode)
        {
            ApplyLyricsOnlyPlacement();
            return;
        }

        if (SettingsPanel.Visibility == Visibility.Visible)
        {
            Width = Math.Max(Width, 760);
            Height = Math.Max(Height, 360);
        }
    }

    private void ApplyLyricsOnlyPlacement()
    {
        _suppressWindowPlacementSave = true;
        try
        {
            Width = Math.Clamp(_settings.LyricsOnlyWidth, 360, 2200);
            Height = Math.Clamp(_settings.LyricsOnlyHeight, 120, 900);
            if (IsFinite(_settings.LyricsOnlyLeft) && IsFinite(_settings.LyricsOnlyTop))
            {
                var workArea = SystemParameters.WorkArea;
                Left = Math.Clamp(_settings.LyricsOnlyLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
                Top = Math.Clamp(_settings.LyricsOnlyTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
            }
        }
        finally
        {
            _suppressWindowPlacementSave = false;
        }
    }

    private void ApplyIslandPlacement()
    {
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 360;
        MinHeight = 68;

        var hasTranslation = _settings.ShowTranslation && !string.IsNullOrWhiteSpace(IslandOriginalText.Text);
        var activeTextLength = Math.Max(IslandMainLyricText.Text?.Length ?? 0, IslandOriginalText.Text?.Length ?? 0);
        var dynamicWidth = _settings.IslandWidth + Math.Clamp(activeTextLength * 5.0, 0, 220);
        var dynamicHeight = _settings.IslandHeight + (hasTranslation ? 18 : 0);
        var targetWidth = Math.Clamp(dynamicWidth, 360, Math.Min(1200, SystemParameters.WorkArea.Width - 48));
        var targetHeight = Math.Clamp(dynamicHeight, 68, 180);

        _suppressWindowPlacementSave = true;
        try
        {
            AnimateWindowSize(targetWidth, SettingsPanel.Visibility == Visibility.Visible
                ? Math.Max(360, targetHeight + 260)
                : targetHeight);

            var workArea = SystemParameters.WorkArea;
            if (_settings.IslandSnapToTop || Top <= workArea.Top + 80)
            {
                Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
                Top = workArea.Top + Math.Clamp(_settings.IslandTopOffset, 0, 120);
            }
            else if (IsFinite(_settings.IslandLeft) && IsFinite(_settings.IslandTop))
            {
                Left = Math.Clamp(_settings.IslandLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
                Top = Math.Clamp(_settings.IslandTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
            }
            else
            {
                Left = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
                Top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
            }
        }
        finally
        {
            _suppressWindowPlacementSave = false;
        }

        _settings.IslandWidth = Math.Clamp(_settings.IslandWidth, 360, 1200);
        _settings.IslandHeight = Math.Clamp(_settings.IslandHeight, 68, 180);
    }

    private void AnimateWindowSize(double width, double height)
    {
        if (!IsLoaded)
        {
            Width = width;
            Height = height;
            return;
        }

        BeginAnimation(WidthProperty, new DoubleAnimation
        {
            To = width,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);

        BeginAnimation(HeightProperty, new DoubleAnimation
        {
            To = height,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void PulseIsland()
    {
        if (!_settings.EnableIslandBreathing || !IsIslandMode || ShellScaleTransform is null)
        {
            return;
        }

        ShellScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            From = 0.985,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);
        ShellScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation
        {
            From = 0.965,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void RememberWindowPlacement()
    {
        if (_suppressWindowPlacementSave || IsIslandMode || WindowState != WindowState.Normal)
        {
            return;
        }

        if (!IsFinite(Left) || !IsFinite(Top) || !IsFinite(Width) || !IsFinite(Height))
        {
            return;
        }

        if (_settings.LyricsOnlyMode)
        {
            _settings.LyricsOnlyLeft = Left;
            _settings.LyricsOnlyTop = Top;
            _settings.LyricsOnlyWidth = Math.Clamp(Width, 360, 2200);
            _settings.LyricsOnlyHeight = Math.Clamp(Height, 120, 900);
        }
        else
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Math.Clamp(Width, 520, 2200);
            _settings.WindowHeight = Math.Clamp(Height, 180, 1400);
        }

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

    private void ToggleSettingsPanel()
    {
        if (SettingsPanel.Visibility != Visibility.Visible)
        {
            ShowWindowAndBringFront();
        }

        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        ApplySettings();
    }

    private void ShowWindowAndBringFront()
    {
        Show();
        WindowState = WindowState.Normal;
        if (IsIslandMode)
        {
            ApplyIslandPlacement();
        }

        Topmost = true;
        EnforceTopmost();
        Activate();
    }

    private void EnforceTopmost()
    {
        if (!IsVisible)
        {
            return;
        }

        Topmost = true;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SetWindowPosFlags.NoMove
            | SetWindowPosFlags.NoSize
            | SetWindowPosFlags.NoActivate);
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
        if (IsIslandMode)
        {
            return;
        }

        RememberWindowPlacementThrottled();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsIslandMode)
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

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ToggleSettingsPanel();

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        ApplySettings();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) => ToggleSettingsPanel();

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
        _settings.LyricsOnlyMode = !_settings.LyricsOnlyMode;
        ApplySettingsToControls();
        SaveAndApplySettings();
        UpdateTrayMenuText();
    }

    private void ShowTranslationCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.ShowTranslation = ShowTranslationCheckBox.IsChecked == true;
        OnTranslationSettingChanged();
        SaveAndApplySettings();
    }

    private void LyricsOnlyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.LyricsOnlyMode = LyricsOnlyCheckBox.IsChecked == true;
        SaveAndApplySettings();
        UpdateTrayMenuText();
    }

    private void VerticalLyricsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.LayoutMode = VerticalLyricsCheckBox.IsChecked == true
            ? LyricsLayoutMode.Vertical
            : LyricsLayoutMode.Center;
        ApplySettingsToControls();
        SaveAndApplySettings();
    }

    private void IslandModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        if (IslandModeCheckBox.IsChecked == true)
        {
            _settings.LayoutMode = LyricsLayoutMode.Island;
            _settings.IslandSnapToTop = true;
            _settings.IslandLeft = double.NaN;
            _settings.IslandTop = double.NaN;
        }
        else if (_settings.LayoutMode == LyricsLayoutMode.Island)
        {
            _settings.LayoutMode = LyricsLayoutMode.Center;
        }

        ApplySettingsToControls();
        SaveAndApplySettings();
    }

    private void AutoScrollLongLyricsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.AutoScrollLongLyrics = AutoScrollLongLyricsCheckBox.IsChecked == true;
        SaveAndApplySettings();
    }

    private void AutoCenterCurrentLyricCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.AutoCenterCurrentLyric = AutoCenterCurrentLyricCheckBox.IsChecked == true;
        SaveAndApplySettings();
    }

    private void AutoLyricsPanelCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.AutoOpenAppleMusicLyricsPanel = AutoLyricsPanelCheckBox.IsChecked == true;
        SaveAndApplySettings();
    }

    private void AutoContrastCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        _settings.AutoContrastText = AutoContrastCheckBox.IsChecked == true;
        SaveAndApplySettings();
    }

    private void InterfaceLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSettings || InterfaceLanguageComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (Enum.TryParse<UiLanguage>(item.Tag?.ToString(), out var language))
        {
            _settings.InterfaceLanguage = language;
            SaveAndApplySettings();
        }
    }

    private void LyricOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings || _settings is null)
        {
            return;
        }

        _settings.LyricOffsetMs = (int)Math.Round(e.NewValue);
        LyricOffsetLabel.Text = $"{_ui.LyricsOffset}: {_ui.LyricOffsetValue(_settings.LyricOffsetMs)}";
        SaveAndApplySettings();
    }

    private void LyricPositionXSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings || _settings is null)
        {
            return;
        }

        _settings.LyricOffsetX = Math.Round(e.NewValue);
        LyricPositionXLabel.Text = $"{_ui.LyricPositionX}: {_ui.LyricPositionValue(_settings.LyricOffsetX)}";
        SaveAndApplySettings();
    }

    private void LyricPositionYSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings || _settings is null)
        {
            return;
        }

        _settings.LyricOffsetY = Math.Round(e.NewValue);
        LyricPositionYLabel.Text = $"{_ui.LyricPositionY}: {_ui.LyricPositionValue(_settings.LyricOffsetY)}";
        SaveAndApplySettings();
    }

    private void MainFontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings || _settings is null)
        {
            return;
        }

        _settings.MainFontSize = e.NewValue;
        SaveAndApplySettings();
    }

    private void OriginalFontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings || _settings is null)
        {
            return;
        }

        _settings.OriginalFontSize = e.NewValue;
        SaveAndApplySettings();
    }

    private void BackgroundOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings || _settings is null)
        {
            return;
        }

        _settings.BackgroundOpacity = e.NewValue / 100.0;
        SaveAndApplySettings();
    }

    private void IslandWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings || _settings is null)
        {
            return;
        }

        _settings.IslandWidth = Math.Round(e.NewValue);
        IslandWidthLabel.Text = $"{_ui.IslandWidth}: {_ui.PixelValue(_settings.IslandWidth)}";
        SaveAndApplySettings();
    }

    private void IslandHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings || _settings is null)
        {
            return;
        }

        _settings.IslandHeight = Math.Round(e.NewValue);
        IslandHeightLabel.Text = $"{_ui.IslandHeight}: {_ui.PixelValue(_settings.IslandHeight)}";
        SaveAndApplySettings();
    }

    private void IslandTopOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings || _settings is null)
        {
            return;
        }

        _settings.IslandTopOffset = Math.Round(e.NewValue);
        _settings.IslandSnapToTop = true;
        IslandTopOffsetLabel.Text = $"{_ui.IslandTopOffset}: {_ui.PixelValue(_settings.IslandTopOffset)}";
        SaveAndApplySettings();
    }

    private void MainColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string color })
        {
            _settings.MainColor = color;
            SaveAndApplySettings();
        }
    }

    private void OriginalColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string color })
        {
            _settings.OriginalColor = color;
            SaveAndApplySettings();
        }
    }

    private void BackgroundColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string color })
        {
            _settings.BackgroundColor = color;
            SaveAndApplySettings();
        }
    }

    private void AccentColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string color })
        {
            _settings.AccentColor = color;
            SaveAndApplySettings();
        }
    }

    private void CustomMainColorButton_Click(object sender, RoutedEventArgs e)
    {
        PickColor(_settings.MainColor, color => _settings.MainColor = color);
    }

    private void CustomOriginalColorButton_Click(object sender, RoutedEventArgs e)
    {
        PickColor(_settings.OriginalColor, color => _settings.OriginalColor = color);
    }

    private void CustomBackgroundColorButton_Click(object sender, RoutedEventArgs e)
    {
        PickColor(_settings.BackgroundColor, color => _settings.BackgroundColor = color);
    }

    private void CustomAccentColorButton_Click(object sender, RoutedEventArgs e)
    {
        PickColor(_settings.AccentColor, color => _settings.AccentColor = color);
    }

    private void PickColor(string current, Action<string> apply)
    {
        var color = ColorFromHex(current, Colors.White);
        using var dialog = new Forms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            Color = Drawing.Color.FromArgb(color.R, color.G, color.B)
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        apply($"#FF{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        SaveAndApplySettings();
    }

    private void ToggleTranslation()
    {
        _settings.ShowTranslation = !_settings.ShowTranslation;
        ApplySettingsToControls();
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
            if (IsIslandMode)
            {
                SnapIslandAfterMove();
            }
            else
            {
                RememberWindowPlacement();
            }
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

        if (SettingsPanel.Visibility == Visibility.Visible)
        {
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
        UpdatePositionSliders();
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

    private void SnapIslandAfterMove()
    {
        var workArea = SystemParameters.WorkArea;
        if (Top <= workArea.Top + 80)
        {
            _settings.IslandSnapToTop = true;
            _settings.IslandTopOffset = Math.Clamp(Math.Round(Top - workArea.Top), 0, 120);
            _settings.IslandLeft = double.NaN;
            _settings.IslandTop = double.NaN;
            ApplyIslandPlacement();
        }
        else
        {
            _settings.IslandSnapToTop = false;
            _settings.IslandLeft = Left;
            _settings.IslandTop = Top;
        }

        _settingsService.Save(_settings);
        ApplyLocalizedText();
        ApplySettingsToControls();
    }

    private void UpdatePositionSliders()
    {
        _applyingSettings = true;
        try
        {
            LyricPositionXSlider.Value = _settings.LyricOffsetX;
            LyricPositionYSlider.Value = _settings.LyricOffsetY;
            LyricPositionXLabel.Text = $"{_ui.LyricPositionX}: {_ui.LyricPositionValue(_settings.LyricOffsetX)}";
            LyricPositionYLabel.Text = $"{_ui.LyricPositionY}: {_ui.LyricPositionValue(_settings.LyricOffsetY)}";
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void SetLyricText(string original, string main, bool animate = true)
    {
        if (!animate || OriginalText.Text == original && MainLyricText.Text == main)
        {
            OriginalText.Text = original;
            MainLyricText.Text = main;
            IslandOriginalText.Text = original;
            IslandMainLyricText.Text = main;
            OriginalText.Opacity = 1;
            MainLyricText.Opacity = 1;
            IslandOriginalText.Opacity = 1;
            IslandMainLyricText.Opacity = 1;
            return;
        }

        AnimateTextChange(OriginalText, original);
        AnimateTextChange(MainLyricText, main);
        AnimateTextChange(IslandOriginalText, original);
        AnimateTextChange(IslandMainLyricText, main);
    }

    private void SetIslandLyricText(string original, string main, bool animate = true)
    {
        if (!animate || IslandOriginalText.Text == original && IslandMainLyricText.Text == main)
        {
            IslandOriginalText.Text = original;
            IslandMainLyricText.Text = main;
            IslandOriginalText.Opacity = 1;
            IslandMainLyricText.Opacity = 1;
            return;
        }

        AnimateTextChange(IslandOriginalText, original);
        AnimateTextChange(IslandMainLyricText, main);
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
        _trayMenu.Items.Add("settings", null, (_, _) => Dispatcher.Invoke(() =>
        {
            ShowWindowAndBringFront();
            SettingsPanel.Visibility = Visibility.Visible;
            ApplySettings();
        }));
        _trayMenu.Items.Add("translation", null, (_, _) => Dispatcher.Invoke(ToggleTranslation));
        _trayMenu.Items.Add("lyrics-only", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _settings.LyricsOnlyMode = false;
            ApplySettingsToControls();
            SaveAndApplySettings();
            ShowWindowAndBringFront();
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
        if (_trayMenu.Items.Count < 7)
        {
            return;
        }

        _trayMenu.Items[0].Text = _ui.ShowWindow;
        _trayMenu.Items[1].Text = _ui.Settings;
        _trayMenu.Items[2].Text = _settings.ShowTranslation ? _ui.ToggleTranslationOn : _ui.ToggleTranslationOff;
        _trayMenu.Items[3].Text = _settings.LyricsOnlyMode ? _ui.ExitLyricsOnly : _ui.LyricsOnlyMode;
        _trayMenu.Items[4].Text = _ui.RescanLyrics;
        _trayMenu.Items[6].Text = _ui.Exit;

        if (_trayIcon is not null)
        {
            _trayIcon.Text = _ui.WindowTitle;
        }
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        RememberWindowPlacement();
        _autoRetryCancellation?.Cancel();
        _loadCancellation?.Cancel();
        _translationCancellation?.Cancel();
        _translationService.FlushCache();
        _settingsService.Save(_settings);
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnClosed(e);
    }

    private static readonly IntPtr HwndTopmost = new(-1);

    [Flags]
    private enum SetWindowPosFlags : uint
    {
        NoSize = 0x0001,
        NoMove = 0x0002,
        NoActivate = 0x0010,
        ShowWindow = 0x0040
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        SetWindowPosFlags flags);

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
