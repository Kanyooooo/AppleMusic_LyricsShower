using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace AppleMusicTranslator.Services;

internal static class AppLogger
{
    private const long MaxLogBytes = 1024 * 1024;
    private const int MaxArchiveCount = 3;
    private static readonly object SyncRoot = new();

    public static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AppleMusicTranslator",
        "logs",
        "app.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message, Exception? exception = null) =>
        Write("WARN", message, exception);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", message, exception);

    public static void Error(Exception exception, string message) =>
        Write("ERROR", message, exception);

    public static void LogStartupInfo()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var assemblyName = assembly.GetName();
            using var process = Process.GetCurrentProcess();

            Info("Application startup information:");
            Info($"  Version: {GetVersion(assembly, assemblyName)}");
            Info($"  OS: {RuntimeInformation.OSDescription} ({Environment.OSVersion.VersionString})");
            Info($"  Runtime: {RuntimeInformation.FrameworkDescription}");
            Info($"  Process: {process.ProcessName} ({process.Id}), {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
            Info($"  Base directory: {AppContext.BaseDirectory}");
            Info($"  Current directory: {Environment.CurrentDirectory}");
            Info($"  Log file: {LogFilePath}");
        }
        catch (Exception ex)
        {
            Warn("Failed to collect startup information.", ex);
        }
    }

    private static string GetVersion(Assembly assembly, AssemblyName assemblyName)
    {
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        return assemblyName.Version?.ToString() ?? "unknown";
    }

    private static void Write(string level, string message, Exception? exception = null)
    {
        try
        {
            lock (SyncRoot)
            {
                var directory = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                TryRotateLog();

                var builder = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                    .Append(" [")
                    .Append(level)
                    .Append("] ")
                    .AppendLine(message);

                if (exception is not null)
                {
                    builder.AppendLine(exception.ToString());
                }

                File.AppendAllText(LogFilePath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never interrupt application startup, shutdown, or exception handling.
        }
    }

    private static void TryRotateLog()
    {
        try
        {
            if (!File.Exists(LogFilePath) || new FileInfo(LogFilePath).Length < MaxLogBytes)
            {
                return;
            }

            var oldestArchive = GetArchivePath(MaxArchiveCount);
            if (File.Exists(oldestArchive))
            {
                File.Delete(oldestArchive);
            }

            for (var index = MaxArchiveCount - 1; index >= 1; index--)
            {
                var source = GetArchivePath(index);
                if (File.Exists(source))
                {
                    File.Move(source, GetArchivePath(index + 1));
                }
            }

            File.Move(LogFilePath, GetArchivePath(1));
        }
        catch
        {
            // If rotation is blocked, keep trying to append to the current log file.
        }
    }

    private static string GetArchivePath(int index) => $"{LogFilePath}.{index}";
}
