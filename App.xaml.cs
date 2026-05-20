using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AppleMusicTranslator.Services;

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
    private bool _exceptionLoggingRegistered;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppLogger.Info("Application startup requested.");
        AppLogger.LogStartupInfo();
        RegisterUnhandledExceptionLogging();

        try
        {
            _singleInstanceMutex = new Mutex(true, MutexName, out var createdNew);
            AppLogger.Info($"Single-instance mutex createdNew={createdNew}.");
            if (!createdNew)
            {
                AppLogger.Info("Another instance is already running; activating existing instance and exiting.");
                ActivateExistingInstance();
                Shutdown();
                return;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Failed to initialize single-instance mutex; continuing startup.");
        }

        _ownsSingleInstanceMutex = _singleInstanceMutex is not null;
        base.OnStartup(e);

        try
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
            AppLogger.Info("Main window shown.");
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Failed to create or show the main window.");
            throw;
        }

        StartWakeListener();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info($"Application exit requested. ExitCode={e.ApplicationExitCode}.");
        _isExiting = true;
        try
        {
            _wakeEvent?.Set();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Failed to signal wake listener during shutdown.", ex);
        }

        if (_ownsSingleInstanceMutex)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
                AppLogger.Info("Single-instance mutex released.");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Failed to release single-instance mutex.", ex);
            }
        }

        _singleInstanceMutex?.Dispose();
        _wakeThread?.Join(300);
        _wakeEvent?.Dispose();
        base.OnExit(e);
    }

    private void RegisterUnhandledExceptionLogging()
    {
        if (_exceptionLoggingRegistered)
        {
            return;
        }

        _exceptionLoggingRegistered = true;

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error(args.Exception, "Unhandled UI dispatcher exception.");
            System.Windows.MessageBox.Show(
                "AppleMusicTranslator encountered an unexpected UI error. Details were written to the log.",
                "AppleMusicTranslator",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception;
            AppLogger.Error(
                $"Unhandled application domain exception. IsTerminating={args.IsTerminating}.",
                exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };
    }

    private static void ActivateExistingInstance()
    {
        try
        {
            using var wakeEvent = EventWaitHandle.OpenExisting(WakeEventName);
            wakeEvent.Set();
            AppLogger.Info("Wake event signaled for existing instance.");
            return;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Wake event activation failed; falling back to foreground window activation.", ex);
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
                AppLogger.Info($"Activated existing process {existing.Id} by window handle.");
            }
            else
            {
                AppLogger.Warn("Existing process was found without an activatable main window handle.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Fallback window activation failed.", ex);
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
                        AppLogger.Info("Wake listener stopped because the wake event was disposed.");
                        return;
                    }
                    catch (InvalidOperationException ex)
                    {
                        AppLogger.Warn("Wake listener stopped because the wake event became invalid.", ex);
                        return;
                    }

                    if (_isExiting)
                    {
                        return;
                    }

                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    {
                        AppLogger.Info("Wake listener ignored activation because dispatcher shutdown has started.");
                        return;
                    }

                    AppLogger.Info("Wake listener received activation request.");
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (MainWindow is MainWindow mainWindow)
                        {
                            AppLogger.Info("Showing main window from wake listener.");
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
            AppLogger.Info("Wake listener started.");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Failed to start wake listener.", ex);
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
