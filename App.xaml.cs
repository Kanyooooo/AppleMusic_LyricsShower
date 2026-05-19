using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace AppleMusicTranslator;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Global\\AppleMusicTranslator.Kanyo.SingleInstance";
    private const string WakeEventName = "Global\\AppleMusicTranslator.Kanyo.Wake";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _wakeEvent;
    private Thread? _wakeThread;
    private bool _ownsSingleInstanceMutex;
    private volatile bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        _ownsSingleInstanceMutex = true;
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
        StartWakeListener();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isExiting = true;
        try
        {
            _wakeEvent?.Set();
        }
        catch
        {
            // Shutdown should never be blocked by the single-instance wake channel.
        }

        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        _wakeThread?.Join(300);
        _wakeEvent?.Dispose();
        base.OnExit(e);
    }

    private static void ActivateExistingInstance()
    {
        try
        {
            using var wakeEvent = EventWaitHandle.OpenExisting(WakeEventName);
            wakeEvent.Set();
            return;
        }
        catch
        {
            // Older builds did not expose the wake event, fall back to window activation.
        }

        try
        {
            using var current = Process.GetCurrentProcess();
            var existing = Process.GetProcessesByName(current.ProcessName)
                .FirstOrDefault(process => process.Id != current.Id);
            if (existing is not null && existing.MainWindowHandle != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(existing.MainWindowHandle, NativeMethods.SwRestore);
                NativeMethods.SetForegroundWindow(existing.MainWindowHandle);
            }
        }
        catch
        {
            // Single-instance activation is a convenience path; startup should still stop cleanly.
        }
    }

    private void StartWakeListener()
    {
        try
        {
            _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
            _wakeThread = new Thread(() =>
            {
                while (!_isExiting)
                {
                    try
                    {
                        _wakeEvent.WaitOne();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (InvalidOperationException)
                    {
                        return;
                    }

                    if (_isExiting)
                    {
                        return;
                    }

                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    {
                        return;
                    }

                    Dispatcher.BeginInvoke(() =>
                    {
                        if (MainWindow is MainWindow mainWindow)
                        {
                            mainWindow.ShowWindowAndBringFront();
                        }
                    }, DispatcherPriority.Normal);
                }
            })
            {
                IsBackground = true,
                Name = "AppleMusicTranslator Wake Listener"
            };
            _wakeThread.Start();
        }
        catch
        {
            // The tray menu still provides access if the wake event cannot be created.
        }
    }

    private static partial class NativeMethods
    {
        public const int SwRestore = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
