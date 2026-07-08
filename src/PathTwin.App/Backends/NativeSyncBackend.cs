using PathTwin.App.Logging;
using PathTwin.App.Services;

namespace PathTwin.App.Backends;

public sealed class NativeSyncBackend : ISyncBackend
{
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);
    private readonly LogService _logService;

    public NativeSyncBackend(LogService logService)
    {
        _logService = logService;
    }

    public string Name => "native-file-system";
    public bool IsAvailable => true;

    public Task<IReadOnlyList<string>> ListDirectoriesAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        var result = new List<string>();
        foreach (var directory in EnumerateDirectoriesSafe(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(PathSafety.GetRelativePath(root, directory));
        }

        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    public Task CopyAsync(
        string source,
        string destination,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default)
        => CopyInternalAsync(source, destination, mirror: false, options, cancellationToken);

    public Task SyncAsync(
        string source,
        string destination,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default)
        => CopyInternalAsync(source, destination, mirror: true, options, cancellationToken);

    public async Task DryRunAsync(
        string source,
        string destination,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default)
    {
        await _logService.AppendAsync(options.LogPath, $"Backend: {Name}", cancellationToken);
        await _logService.AppendAsync(options.LogPath, $"Dry run: {source} -> {destination}", cancellationToken);

        var sourceFiles = Directory.Exists(source)
            ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Count()
            : 0;
        var destinationFiles = Directory.Exists(destination)
            ? Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Count()
            : 0;

        await _logService.AppendAsync(options.LogPath, $"Source files: {sourceFiles}", cancellationToken);
        await _logService.AppendAsync(options.LogPath, $"Destination files: {destinationFiles}", cancellationToken);
    }

    private async Task CopyInternalAsync(
        string source,
        string destination,
        bool mirror,
        SyncBackendOptions options,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Source directory does not exist: {source}");
        }

        Directory.CreateDirectory(destination);
        await _logService.AppendAsync(options.LogPath, $"Backend: {Name}", cancellationToken);
        await _logService.AppendAsync(options.LogPath, $"{(mirror ? "Mirror" : "Copy")}: {source} -> {destination}", cancellationToken);

        var copied = 0;
        if (options.CreateEmptyDirectories)
        {
            foreach (var directory in EnumerateDirectoriesSafe(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = PathSafety.GetRelativePath(source, directory);
                Directory.CreateDirectory(PathSafety.CombineRootAndRelative(destination, relative));
            }
        }

        foreach (var file in EnumerateFilesSafe(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = PathSafety.GetRelativePath(source, file);
            var target = PathSafety.CombineRootAndRelative(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);

            if (ShouldCopy(file, target))
            {
                PathSafety.EnsureInsideRoot(destination, target, "copy");
                File.Copy(file, target, overwrite: true);
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
                copied++;
            }
        }

        var deleted = 0;
        if (mirror)
        {
            foreach (var destinationFile in EnumerateFilesSafe(destination))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = PathSafety.GetRelativePath(destination, destinationFile);
                var sourceFile = PathSafety.CombineRootAndRelative(source, relative);
                if (!File.Exists(sourceFile))
                {
                    PathSafety.EnsureInsideRoot(destination, destinationFile, "delete");
                    File.Delete(destinationFile);
                    deleted++;
                }
            }

            foreach (var destinationDirectory in EnumerateDirectoriesSafe(destination).OrderByDescending(path => path.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = PathSafety.GetRelativePath(destination, destinationDirectory);
                var sourceDirectory = PathSafety.CombineRootAndRelative(source, relative);
                if (!Directory.Exists(sourceDirectory) && Directory.Exists(destinationDirectory))
                {
                    PathSafety.EnsureInsideRoot(destination, destinationDirectory, "delete");
                    if (!Directory.EnumerateFileSystemEntries(destinationDirectory).Any())
                    {
                        Directory.Delete(destinationDirectory);
                    }
                }
            }
        }

        await _logService.AppendAsync(options.LogPath, $"Copied/updated: {copied}; deleted: {deleted}", cancellationToken);
    }

    private static bool ShouldCopy(string sourceFile, string destinationFile)
    {
        if (!File.Exists(destinationFile))
        {
            return true;
        }

        var source = new FileInfo(sourceFile);
        var destination = new FileInfo(destinationFile);
        if (source.Length != destination.Length)
        {
            return true;
        }

        return (source.LastWriteTimeUtc - destination.LastWriteTimeUtc).Duration() > TimestampTolerance;
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        }))
        {
            yield return directory;
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        }))
        {
            yield return file;
        }
    }
}
