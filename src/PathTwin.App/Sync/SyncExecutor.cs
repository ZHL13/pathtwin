using PathTwin.App.Logging;
using PathTwin.App.Models;
using PathTwin.App.Services;

namespace PathTwin.App.Sync;

public sealed class SyncExecutor
{
    private readonly LogService _logService;

    public SyncExecutor(LogService logService)
    {
        _logService = logService;
    }

    public async Task ExecuteAsync(
        SyncPlan plan,
        WorkSession session,
        string historyRoot,
        string logPath,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(historyRoot);

        var operations = plan.Operations.Where(o => o.Kind != SyncOperationKind.Skip).ToList();
        var completed = 0;
        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remotePath = PathSafety.CombineRootAndRelative(session.RemoteRoot, operation.RelativePath);
            var localPath = PathSafety.CombineRootAndRelative(session.LocalRoot, operation.RelativePath);

            progress?.Report(new SyncProgress
            {
                Phase = $"Syncing ({completed + 1}/{operations.Count})",
                Detail = operation.RelativePath,
                Completed = completed,
                Total = operations.Count
            });

            switch (operation.Kind)
            {
                case SyncOperationKind.UploadNew:
                case SyncOperationKind.OverwriteRemote:
                    await UploadAsync(operation, session, localPath, remotePath, historyRoot, logPath, cancellationToken);
                    break;
                case SyncOperationKind.DeleteRemote:
                    await DeleteRemoteAsync(operation, session, remotePath, historyRoot, logPath, cancellationToken);
                    break;
                case SyncOperationKind.Skip:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation.Kind), operation.Kind, "Unsupported sync operation.");
            }

            completed++;
        }
    }

    private async Task UploadAsync(
        SyncOperation operation,
        WorkSession session,
        string localPath,
        string remotePath,
        string historyRoot,
        string logPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException("Local file disappeared before upload.", localPath);
        }

        PathSafety.EnsureInsideRoot(session.LocalRoot, localPath, "read local file");
        PathSafety.EnsureInsideRoot(session.RemoteRoot, remotePath, "write remote file");

        if (File.Exists(remotePath))
        {
            await BackupRemoteFileAsync(session, remotePath, operation.RelativePath, historyRoot, "overwritten", logPath, cancellationToken);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(remotePath) ?? session.RemoteRoot);
        File.Copy(localPath, remotePath, overwrite: true);
        File.SetLastWriteTimeUtc(remotePath, File.GetLastWriteTimeUtc(localPath));

        // Also archive a copy of the uploaded local file in history for traceability
        await BackupLocalUploadedFileAsync(session, localPath, operation.RelativePath, historyRoot, "uploaded", logPath, cancellationToken);

        await _logService.AppendAsync(logPath, $"Uploaded: {operation.RelativePath}", cancellationToken);
    }

    private async Task DeleteRemoteAsync(
        SyncOperation operation,
        WorkSession session,
        string remotePath,
        string historyRoot,
        string logPath,
        CancellationToken cancellationToken)
    {
        PathSafety.EnsureInsideRoot(session.RemoteRoot, remotePath, "delete remote file");
        if (!File.Exists(remotePath))
        {
            await _logService.AppendAsync(logPath, $"Remote already absent: {operation.RelativePath}", cancellationToken);
            return;
        }

        await BackupRemoteFileAsync(session, remotePath, operation.RelativePath, historyRoot, "deleted", logPath, cancellationToken);
        File.Delete(remotePath);
        await _logService.AppendAsync(logPath, $"Deleted remote: {operation.RelativePath}", cancellationToken);
    }

    private async Task BackupRemoteFileAsync(
        WorkSession session,
        string remotePath,
        string relativePath,
        string historyRoot,
        string bucket,
        string logPath,
        CancellationToken cancellationToken)
    {
        var historySessionRoot = Path.Combine(historyRoot, $"{DateTime.Now:yyyy-MM-dd_HHmm}_EndWork");
        var backupPath = PathSafety.CombineRootAndRelative(Path.Combine(historySessionRoot, bucket), relativePath);
        backupPath = CreateUniquePath(backupPath);

        PathSafety.EnsureInsideRoot(historyRoot, backupPath, "write history backup");
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath) ?? historySessionRoot);
        File.Copy(remotePath, backupPath, overwrite: false);
        File.SetLastWriteTimeUtc(backupPath, File.GetLastWriteTimeUtc(remotePath));
        await _logService.AppendAsync(logPath, $"Backed up remote {relativePath} -> {backupPath}", cancellationToken);
    }

    private async Task BackupLocalUploadedFileAsync(
        WorkSession session,
        string localPath,
        string relativePath,
        string historyRoot,
        string bucket,
        string logPath,
        CancellationToken cancellationToken)
    {
        var historySessionRoot = Path.Combine(historyRoot, $"{DateTime.Now:yyyy-MM-dd_HHmm}_EndWork");
        var backupPath = PathSafety.CombineRootAndRelative(Path.Combine(historySessionRoot, bucket), relativePath);
        backupPath = CreateUniquePath(backupPath);

        PathSafety.EnsureInsideRoot(historyRoot, backupPath, "write uploaded history copy");
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath) ?? historySessionRoot);
        File.Copy(localPath, backupPath, overwrite: false);
        File.SetLastWriteTimeUtc(backupPath, File.GetLastWriteTimeUtc(localPath));
        await _logService.AppendAsync(logPath, $"Archived uploaded copy {relativePath} -> {backupPath}", cancellationToken);
    }

    private static string CreateUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{name}.{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}.{DateTime.Now:yyyyMMddHHmmssfff}{extension}");
    }
}
