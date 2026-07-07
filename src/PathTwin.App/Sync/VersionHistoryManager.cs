using System.Globalization;
using PathTwin.App.Logging;
using PathTwin.App.Services;

namespace PathTwin.App.Sync;

public sealed class VersionHistoryManager
{
    private readonly LogService _logService;

    public VersionHistoryManager(LogService logService)
    {
        _logService = logService;
    }

    public async Task CleanOldHistoryAsync(
        string historyRoot,
        int retentionDays,
        string logPath,
        CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0 || !Directory.Exists(historyRoot))
        {
            return;
        }

        var cutoff = DateTime.Now.AddDays(-retentionDays);
        foreach (var directory in Directory.EnumerateDirectories(historyRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (!name.EndsWith("_EndWork", StringComparison.OrdinalIgnoreCase) || name.Length < 23)
            {
                continue;
            }

            var stamp = name[..15];
            if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var createdAt))
            {
                continue;
            }

            if (createdAt >= cutoff)
            {
                continue;
            }

            PathSafety.EnsureInsideRoot(historyRoot, directory, "clean old history");
            Directory.Delete(directory, recursive: true);
            await _logService.AppendAsync(logPath, $"Cleaned old history: {directory}", cancellationToken);
        }
    }
}
