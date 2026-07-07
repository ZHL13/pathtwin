using PathTwin.App.Models;

namespace PathTwin.App.Logging;

public sealed class LogService
{
    public async Task AppendAsync(string logPath, string message, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? ".");
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {message}{Environment.NewLine}";
        await File.AppendAllTextAsync(logPath, line, cancellationToken);
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
        await AppendAsync(logPath, $"Plan: upload={plan.UploadCount}, delete={plan.DeleteCount}, skip={plan.SkipCount}, conflicts={plan.Conflicts.Count}", cancellationToken);
        foreach (var operation in plan.Operations)
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
