using System.Diagnostics;

namespace AppleMusicTranslator.Services;

public sealed class AppleMusicProcessFinder
{
    private static readonly string[] ProcessNames =
    [
        "AppleMusic",
        "Music",
        "iTunes"
    ];

    public int? FindProcessId()
    {
        foreach (var processName in ProcessNames)
        {
            try
            {
                var process = Process.GetProcessesByName(processName).FirstOrDefault(p => !p.HasExited);
                if (process is not null)
                {
                    return process.Id;
                }
            }
            catch
            {
                // Ignore stale process handles and keep trying known names.
            }
        }

        return null;
    }
}
