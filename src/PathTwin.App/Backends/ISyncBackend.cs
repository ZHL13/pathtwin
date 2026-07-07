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

    Task DryRunAsync(
        string source,
        string destination,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class SyncBackendOptions
{
    public string LogPath { get; init; } = string.Empty;
    public bool Mirror { get; init; }
    public bool CreateEmptyDirectories { get; init; } = true;
}
