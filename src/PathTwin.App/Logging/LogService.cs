using System.Collections.Concurrent;
using PathTwin.App.Models;

namespace PathTwin.App.Logging;

public sealed class LogService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AppendLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task AppendAsync(string logPath, string message, CancellationToken cancellationToken = default)
    {
        var appendLock = AppendLocks.GetOrAdd(Path.GetFullPath(logPath), static _ => new SemaphoreSlim(1, 1));
        await appendLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? ".");
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {message}{Environment.NewLine}";
            await File.AppendAllTextAsync(logPath, line, cancellationToken);
        }
        finally
        {
            appendLock.Release();
        }
    }

    public async Task WriteSessionHeaderAsync(
        string logPath,
        WorkSession session,
        string operation,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? ".");
        var lines = new[]
        {
            $"Operation: {operation}",
            $"Session: {session.SessionId}",
            $"Started: {session.StartedAt:O}",
            $"Remote root: {session.RemoteRoot}",
            $"Local root: {session.LocalRoot}",
            $"Selected paths: {string.Join(", ", session.SelectedPaths)}",
            string.Empty
        };

        await File.AppendAllLinesAsync(logPath, lines, cancellationToken);
    }

    public async Task WritePlanSummaryAsync(
        string logPath,
        SyncPlan plan,
        CancellationToken cancellationToken = default)
    {
        await AppendAsync(logPath, $"Plan: upload={plan.UploadCount}, delete={plan.DeleteCount}, conflicts={plan.Conflicts.Count}", cancellationToken);
        foreach (var operation in plan.Operations.Where(operation => operation.Kind != SyncOperationKind.Skip))
        {
            await AppendAsync(logPath, $"{operation.Kind}: {operation.RelativePath} - {operation.Reason}", cancellationToken);
        }

        foreach (var conflict in plan.Conflicts)
        {
            await AppendAsync(logPath, $"CONFLICT: {conflict.RelativePath} - {conflict.Reason}", cancellationToken);
        }
    }

    public async Task WriteExceptionAsync(string logPath, Exception exception, CancellationToken cancellationToken = default)
    {
        await AppendAsync(logPath, $"ERROR: {exception.GetType().Name}: {exception.Message}", cancellationToken);
        await AppendAsync(logPath, exception.StackTrace ?? "(no stack trace)", cancellationToken);
    }
}
