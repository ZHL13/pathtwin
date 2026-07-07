using Avalonia;
using Avalonia.Fonts.Inter;
using PathTwin.App.Configuration;
using PathTwin.App.Platform;

namespace PathTwin.App;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var instance = SingleInstanceService.TryAcquire();
        if (!instance.HasHandle)
        {
            return; // another instance is already running
        }

        var isAuto = args.Any(a => string.Equals(a, "--auto", StringComparison.OrdinalIgnoreCase));
        if (isAuto && !ShouldStartFromAutoLaunch())
        {
            return; // outside time window or remote unreachable
        }

        if (isAuto)
        {
            WriteAutoLaunchLog("STARTED — all checks passed, showing window.");
        }

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    private static bool ShouldStartFromAutoLaunch()
    {
        string? rejectReason = null;
        try
        {
            var configService = new ConfigService();
            var config = configService.LoadAsync().GetAwaiter().GetResult();
            var profile = config.ActiveProfile;

            if (!profile.EnableAutomaticStartup)
            {
                rejectReason = "EnableAutomaticStartup is false";
                return false;
            }

            var now = TimeOnly.FromDateTime(DateTime.Now);
            if (TimeOnly.TryParse(profile.StartupWindowStart, out var windowStart)
                && TimeOnly.TryParse(profile.StartupWindowEnd, out var windowEnd))
            {
                if (now < windowStart || now > windowEnd)
                {
                    rejectReason = $"Current time {now:HH:mm} is outside window {windowStart:HH:mm}–{windowEnd:HH:mm}";
                    return false;
                }
            }
            else
            {
                rejectReason = $"Failed to parse time window: start='{profile.StartupWindowStart}' end='{profile.StartupWindowEnd}'";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(profile.RemoteRoot)
                && !Directory.Exists(profile.RemoteRoot))
            {
                rejectReason = $"Remote root not reachable: {profile.RemoteRoot}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            rejectReason = $"Exception: {ex.Message}";
            return false;
        }
        finally
        {
            if (rejectReason is not null)
            {
                WriteAutoLaunchLog(rejectReason);
            }
        }
    }

    private static void WriteAutoLaunchLog(string reason)
    {
        try
        {
            var logDir = Path.Combine(Path.GetTempPath(), "PathTwin");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "auto_launch.log");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  SKIPPED  {reason}\n";
            File.AppendAllText(logPath, line);
        }
        catch
        {
            // best-effort logging
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
