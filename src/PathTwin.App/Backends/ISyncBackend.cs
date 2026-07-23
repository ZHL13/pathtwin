using PathTwin.App.Models;

namespace PathTwin.App.Backends;

public interface ISyncBackend
{
    string Name { get; }
    bool IsAvailable { get; }

    Task<IReadOnlyList<string>> ListDirectoriesAsync(
        string root,
        CancellationToken cancellationToken = default);

    Task CopyAsync(
        string source,
        string destination,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default);

    Task SyncAsync(
        string source,
        string destination,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default);

    Task CopyFileAsync(
        string sourceFile,
        string destinationFile,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default);

    Task DeleteFileAsync(
        string destinationFile,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default);

    Task DryRunAsync(
        string source,
        string destination,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default);
}

public interface IRemoteFileScanBackend
{
    Task<IReadOnlyList<FileState>> ScanFilesAsync(
        string root,
        IReadOnlyCollection<string> selectedRelativePaths,
        bool includeHashes,
        string logPath,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class SyncBackendOptions
{
    public string LogPath { get; init; } = string.Empty;
    public bool Mirror { get; init; }
    public bool CreateEmptyDirectories { get; init; } = true;
    public IProgress<SyncProgress>? Progress { get; init; }
    public string ProgressPhase { get; init; } = "Syncing files";
    public string ProgressPathPrefix { get; init; } = string.Empty;
    public ComparisonMode ComparisonMode { get; init; } = ComparisonMode.Hybrid;
}
