using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace AppleMusicTranslator;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Global\\AppleMusicTranslator.Kanyo.SingleInstance";
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void ActivateExistingInstance()
    {
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
