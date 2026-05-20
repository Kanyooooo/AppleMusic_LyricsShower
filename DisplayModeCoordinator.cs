using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AppleMusicTranslator.Models;
using AppleMusicTranslator.Services;

namespace AppleMusicTranslator;

internal sealed class DisplayModeCoordinator
{
    private readonly MainWindow _owner;
    private readonly AppSettings _settings;
    private readonly Func<UiText> _uiProvider;
    private readonly Func<LyricDisplayPayload> _payloadProvider;
    private readonly Action _applyNormalPlacement;
    private readonly Action _rememberNormalPlacement;
    private readonly Action _invalidateCurrentDisplay;

    private LyricsOnlyWindow? _lyricsOnlyWindow;
    private IslandLyricsWindow? _islandWindow;

    public DisplayModeCoordinator(
        MainWindow owner,
        AppSettings settings,
        Func<UiText> uiProvider,
        Func<LyricDisplayPayload> payloadProvider,
        Action applyNormalPlacement,
        Action rememberNormalPlacement,
        Action invalidateCurrentDisplay)
    {
        _owner = owner;
        _settings = settings;
        _uiProvider = uiProvider;
        _payloadProvider = payloadProvider;
        _applyNormalPlacement = applyNormalPlacement;
        _rememberNormalPlacement = rememberNormalPlacement;
        _invalidateCurrentDisplay = invalidateCurrentDisplay;
        NormalizeSettings();
    }

    public WindowShellMode CurrentMode => ResolveMode();

    public LyricsLayoutMode ChildLayoutMode =>
        CurrentMode == WindowShellMode.Island ? LyricsLayoutMode.Island : _settings.LayoutMode;

    public void NormalizeSettings()
    {
        if (_settings.LayoutMode == LyricsLayoutMode.Island)
        {
            _settings.LyricsOnlyMode = false;
        }
    }

    public bool SetMode(WindowShellMode mode)
    {
        NormalizeSettings();
        var previousMode = CurrentMode;
        if (previousMode == mode)
        {
            return false;
        }

        if (previousMode == WindowShellMode.Normal)
        {
            _rememberNormalPlacement();
        }

        switch (mode)
        {
            case WindowShellMode.Normal:
                _settings.LyricsOnlyMode = false;
                _settings.LayoutMode = LyricsLayoutMode.Center;
                break;
            case WindowShellMode.LyricsOnly:
                _settings.LyricsOnlyMode = true;
                if (_settings.LayoutMode == LyricsLayoutMode.Island)
                {
                    _settings.LayoutMode = LyricsLayoutMode.Center;
                }
                break;
            case WindowShellMode.Island:
                _settings.LyricsOnlyMode = false;
                _settings.LayoutMode = LyricsLayoutMode.Island;
                break;
        }

        _owner.WindowStyle = WindowStyle.None;
        _invalidateCurrentDisplay();
        return true;
    }

    public void ApplyHostMode()
    {
        NormalizeSettings();
        if (!_owner.IsLoaded)
        {
            if (CurrentMode == WindowShellMode.Normal)
            {
                _applyNormalPlacement();
            }

            return;
        }

        switch (CurrentMode)
        {
            case WindowShellMode.Normal:
                CloseLyricsOnlyWindow();
                CloseIslandWindow();
                ShowMainDisplayWindow();
                break;
            case WindowShellMode.LyricsOnly:
                CloseIslandWindow();
                EnsureLyricsOnlyWindow();
                ApplyDisplayToWindow(_lyricsOnlyWindow);
                ShowChildDisplayWindow(_lyricsOnlyWindow);
                HideMainDisplayWindow();
                break;
            case WindowShellMode.Island:
                CloseLyricsOnlyWindow();
                EnsureIslandWindow();
                ApplyDisplayToWindow(_islandWindow);
                ShowChildDisplayWindow(_islandWindow);
                HideMainDisplayWindow();
                break;
        }
    }

    public void ApplyChildWindowSettings()
    {
        var payload = _payloadProvider();
        ApplyDisplayToWindow(_lyricsOnlyWindow, payload);
        ApplyDisplayToWindow(_islandWindow, payload);
    }

    public void UpdateActiveChild(LyricDisplayPayload payload)
    {
        switch (CurrentMode)
        {
            case WindowShellMode.LyricsOnly:
                _lyricsOnlyWindow?.UpdateDisplay(payload);
                break;
            case WindowShellMode.Island:
                _islandWindow?.UpdateDisplay(payload);
                break;
        }
    }

    public void ShowAndBringFront()
    {
        switch (CurrentMode)
        {
            case WindowShellMode.Normal:
                CloseLyricsOnlyWindow();
                CloseIslandWindow();
                ShowMainDisplayWindow();
                BringWindowToFront(_owner);
                break;
            case WindowShellMode.LyricsOnly:
                CloseIslandWindow();
                EnsureLyricsOnlyWindow();
                ApplyDisplayToWindow(_lyricsOnlyWindow);
                ShowChildDisplayWindow(_lyricsOnlyWindow);
                HideMainDisplayWindow();
                BringWindowToFront(_lyricsOnlyWindow);
                break;
            case WindowShellMode.Island:
                CloseLyricsOnlyWindow();
                EnsureIslandWindow();
                ApplyDisplayToWindow(_islandWindow);
                ShowChildDisplayWindow(_islandWindow);
                HideMainDisplayWindow();
                BringWindowToFront(_islandWindow);
                break;
        }
    }

    public void EnforceTopmost()
    {
        EnforceTopmost(_owner);
        EnforceTopmost(_lyricsOnlyWindow);
        EnforceTopmost(_islandWindow);
    }

    public void CloseChildWindows()
    {
        CloseLyricsOnlyWindow();
        CloseIslandWindow();
    }

    private WindowShellMode ResolveMode()
    {
        if (_settings.LayoutMode == LyricsLayoutMode.Island)
        {
            return WindowShellMode.Island;
        }

        return _settings.LyricsOnlyMode
            ? WindowShellMode.LyricsOnly
            : WindowShellMode.Normal;
    }

    private void EnsureLyricsOnlyWindow()
    {
        if (_lyricsOnlyWindow is not null)
        {
            return;
        }

        var window = new LyricsOnlyWindow(_owner, _settings);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_lyricsOnlyWindow, window))
            {
                _lyricsOnlyWindow = null;
            }
        };
        _lyricsOnlyWindow = window;
    }

    private void EnsureIslandWindow()
    {
        if (_islandWindow is not null)
        {
            return;
        }

        var window = new IslandLyricsWindow(_owner, _settings);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_islandWindow, window))
            {
                _islandWindow = null;
            }
        };
        _islandWindow = window;
    }

    private void ApplyDisplayToWindow(LyricsOnlyWindow? window)
    {
        if (window is null)
        {
            return;
        }

        ApplyDisplayToWindow(window, _payloadProvider());
    }

    private void ApplyDisplayToWindow(IslandLyricsWindow? window)
    {
        if (window is null)
        {
            return;
        }

        ApplyDisplayToWindow(window, _payloadProvider());
    }

    private void ApplyDisplayToWindow(LyricsOnlyWindow? window, LyricDisplayPayload payload)
    {
        if (window is null)
        {
            return;
        }

        window.ApplySettings(_uiProvider());
        window.UpdateDisplay(payload);
    }

    private void ApplyDisplayToWindow(IslandLyricsWindow? window, LyricDisplayPayload payload)
    {
        if (window is null)
        {
            return;
        }

        window.ApplySettings(_uiProvider());
        window.UpdateDisplay(payload);
    }

    private void ShowMainDisplayWindow()
    {
        if (!_owner.IsVisible)
        {
            _owner.Show();
        }

        _owner.WindowState = WindowState.Normal;
        _owner.ResizeMode = ResizeMode.CanResize;
        _owner.MinWidth = 520;
        _owner.MinHeight = 180;
        _applyNormalPlacement();
        _owner.Topmost = true;
    }

    private void HideMainDisplayWindow()
    {
        _owner.Hide();
    }

    private static void ShowChildDisplayWindow(Window? window)
    {
        if (window is null)
        {
            return;
        }

        window.Owner = null;
        if (!window.IsVisible)
        {
            window.Show();
        }

        window.Topmost = true;
    }

    private void CloseLyricsOnlyWindow()
    {
        if (_lyricsOnlyWindow is null)
        {
            return;
        }

        var window = _lyricsOnlyWindow;
        _lyricsOnlyWindow = null;
        window.Close();
    }

    private void CloseIslandWindow()
    {
        if (_islandWindow is null)
        {
            return;
        }

        var window = _islandWindow;
        _islandWindow = null;
        window.Close();
    }

    private static void BringWindowToFront(Window? window)
    {
        if (window is null)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        window.WindowState = WindowState.Normal;
        window.Topmost = true;
        window.Activate();
    }

    private static void EnforceTopmost(Window? window)
    {
        if (window is not { IsVisible: true })
        {
            return;
        }

        window.Topmost = true;
        var handle = new WindowInteropHelper(window).Handle;
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
        SetWindowPosFlags uFlags);
}
