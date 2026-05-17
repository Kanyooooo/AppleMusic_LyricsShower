using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AppleMusicTranslator.Models;
using AppleMusicTranslator.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfBrushes = System.Windows.Media.Brushes;

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

    public MainWindow()
    {
        InitializeComponent();

        _settings = _settingsService.Load();
        _ui = UiText.For(_settings.InterfaceLanguage);
        ApplyLocalizedText();
        ApplySettingsToControls();
        ApplySettings();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _timer.Tick += Timer_Tick;

        _trayIcon = CreateTrayIcon();
        _trayIcon.Visible = true;
    }

    private TimeSpan LyricOffset => TimeSpan.FromMilliseconds(_settings.LyricOffsetMs);

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
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
        SetLyricText(string.Empty, _ui.LoadingLyrics, animate: false);
        StatusText.Text = _ui.ScanningMemory;
        SourceText.Text = _ui.AppleMusicMemorySource;

        _ = LoadLyricsAsync(track, _loadCancellation.Token, forceReload, allowAutoRetry);
    }

    private async Task LoadLyricsAsync(
        TrackInfo track,
        CancellationToken cancellationToken,
        bool forceReload,
        bool allowAutoRetry)
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
            _lastDisplayedLine = string.Empty;
            SourceText.Text = _ui.SourceFor(lyrics.Source);
            StatusText.Text = _ui.LoadedLyricLines(lyrics.Lines.Count);
            LoadCachedTranslations(lyrics, clearTranslations);
            UpdateCurrentLyric(_latestTrack);
        }).Task;
    }

    private async Task<LyricsBundle> FindMemoryLyricsAsync(TrackInfo track, bool forceReload, CancellationToken cancellationToken)
    {
        var processId = _processFinder.FindProcessId();
        if (processId is null)
        {
            return LyricsBundle.Empty(_ui.AppleMusicProcessNotFound);
        }

        var visibleAnchors = await Task.Run(() => _anchorService.FindVisibleAnchors(processId.Value, track), cancellationToken);
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
            : $"{line.Begin.TotalMilliseconds:0}:{line.Text}:{translated}:{_settings.ShowTranslation}:{_settings.LyricsOnlyMode}";

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

        lock (_translationLock)
        {
            _translations.Clear();
            _translationInFlight.Clear();
        }

        SetLyricText(string.Empty, message, animate: false);
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
            MainFontSlider.Value = _settings.MainFontSize;
            OriginalFontSlider.Value = _settings.OriginalFontSize;
            BackgroundOpacitySlider.Value = _settings.BackgroundOpacity * 100;
            LyricOffsetSlider.Value = _settings.LyricOffsetMs;
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

        MainLyricText.FontSize = _settings.MainFontSize;
        MainLyricText.LineHeight = Math.Max(_settings.MainFontSize * 1.24, _settings.MainFontSize + 4);
        OriginalText.FontSize = _settings.OriginalFontSize;
        MainLyricText.Foreground = BrushFromHex(_settings.MainColor, Colors.White);
        OriginalText.Foreground = BrushFromHex(_settings.OriginalColor, Colors.White);

        HeaderGrid.Visibility = _settings.LyricsOnlyMode ? Visibility.Collapsed : Visibility.Visible;
        FooterGrid.Visibility = _settings.LyricsOnlyMode ? Visibility.Collapsed : Visibility.Visible;
        SettingsPanel.Visibility = _settings.LyricsOnlyMode && !IsActive
            ? Visibility.Collapsed
            : SettingsPanel.Visibility;

        Shell.Padding = _settings.LyricsOnlyMode ? new Thickness(8) : new Thickness(18);
        Shell.Margin = _settings.LyricsOnlyMode ? new Thickness(0) : new Thickness(12);
        Shell.BorderThickness = _settings.LyricsOnlyMode ? new Thickness(0) : new Thickness(1);
        Shell.CornerRadius = _settings.LyricsOnlyMode ? new CornerRadius(0) : new CornerRadius(8);
        Shell.Background = _settings.LyricsOnlyMode
            ? WpfBrushes.Transparent
            : new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)Math.Clamp(_settings.BackgroundOpacity * 255, 0, 255), 24, 27, 34));
        Shell.BorderBrush = BrushFromHex(WithAlpha(_settings.AccentColor, _settings.BorderOpacity), System.Windows.Media.Colors.Cyan);

        LyricGrid.Margin = _settings.LyricsOnlyMode ? new Thickness(0) : new Thickness(0, 18, 0, 12);
        UpdateCurrentLyric(_latestTrack, force: true);
    }

    private void ApplyLocalizedText()
    {
        Title = _ui.WindowTitle;
        TrackText.Text = _latestTrack.HasSongIdentity ? _ui.TrackDisplay(_latestTrack.Title, _latestTrack.Artist) : _ui.WaitingForAppleMusicTitle;
        SettingsButton.Content = _ui.Settings;
        SettingsButton.ToolTip = _ui.SettingsTooltip;
        SettingsPanelTitle.Text = _ui.DisplaySettings;
        ShowTranslationCheckBox.Content = _ui.ShowTranslation;
        LyricsOnlyCheckBox.Content = _ui.LyricsOnlyWindow;
        InterfaceLanguageLabel.Text = _ui.InterfaceLanguage;
        LyricOffsetLabel.Text = $"{_ui.LyricsOffset}: {_ui.LyricOffsetValue(_settings.LyricOffsetMs)}";
        LyricOffsetHintText.Text = _ui.LyricsOffsetHint;
        MainFontLabel.Text = _ui.MainTextSize;
        OriginalFontLabel.Text = _ui.OriginalTextSize;
        BackgroundOpacityLabel.Text = _ui.BackgroundOpacity;
        MainColorLabel.Text = _ui.MainColor;
        OriginalColorLabel.Text = _ui.OriginalColor;
        RefreshButton.ToolTip = _ui.RescanLyricsTooltip;
        MinimizeButton.ToolTip = _ui.Minimize;
        CloseButton.ToolTip = _ui.HideWindow;

        ContextSettingsMenuItem.Header = _ui.Settings;
        ContextTranslationMenuItem.Header = _settings.ShowTranslation ? _ui.ToggleTranslationOn : _ui.ToggleTranslationOff;
        ContextLyricsOnlyMenuItem.Header = _ui.LyricsOnlyMode;
        ContextExitMenuItem.Header = _ui.Exit;

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

    private static string WithAlpha(string value, double opacity)
    {
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
            color.A = (byte)Math.Clamp(opacity * 255, 0, 255);
            return color.ToString();
        }
        catch
        {
            return "#665DFFE6";
        }
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
    }

    private void ShowWindowAndBringFront()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = false;
        Topmost = true;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_latestTrack.HasSongIdentity)
        {
            BeginLoadLyrics(_latestTrack, forceReload: true);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ToggleSettingsPanel();

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = Visibility.Collapsed;

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) => ToggleSettingsPanel();

    private void CloseMenuItem_Click(object sender, RoutedEventArgs e) => ExitApplication();

    private void ToggleTranslationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ToggleTranslation();
    }

    private void LyricsOnlyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.LyricsOnlyMode = !_settings.LyricsOnlyMode;
        ApplySettingsToControls();
        SaveAndApplySettings();
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
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void SetLyricText(string original, string main, bool animate = true)
    {
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
            Icon = Drawing.SystemIcons.Application,
            ContextMenuStrip = _trayMenu
        };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindowAndBringFront);
        UpdateTrayMenuText();
        return tray;
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
        _trayMenu.Items[3].Text = _ui.ExitLyricsOnly;
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
        _autoRetryCancellation?.Cancel();
        _loadCancellation?.Cancel();
        _translationCancellation?.Cancel();
        _translationService.FlushCache();
        _settingsService.Save(_settings);
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnClosed(e);
    }
}
