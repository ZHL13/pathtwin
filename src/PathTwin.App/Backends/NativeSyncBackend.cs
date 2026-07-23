using System.Security.Cryptography;
using PathTwin.App.Logging;
using PathTwin.App.Models;
using PathTwin.App.Services;

namespace PathTwin.App.Backends;

public sealed class NativeSyncBackend : ISyncBackend
{
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);
    private const int ProgressReportInterval = 128;
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

    public async Task CopyFileAsync(
        string sourceFile,
        string destinationFile,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(sourceFile))
        {
            throw new FileNotFoundException("Source file does not exist.", sourceFile);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile) ?? ".");
        File.Copy(sourceFile, destinationFile, overwrite: true);
        File.SetLastWriteTimeUtc(destinationFile, File.GetLastWriteTimeUtc(sourceFile));
        await _logService.AppendAsync(options.LogPath, $"Copied file: {sourceFile} -> {destinationFile}", cancellationToken);
    }

    public async Task DeleteFileAsync(
        string destinationFile,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(destinationFile))
        {
            File.Delete(destinationFile);
            await _logService.AppendAsync(options.LogPath, $"Deleted file: {destinationFile}", cancellationToken);
        }
    }

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
        ReportCheckProgress(options, "Preparing folder structure", "Starting");

        var copied = 0;
        var directoriesChecked = 0;
        if (options.CreateEmptyDirectories)
        {
            foreach (var directory in EnumerateDirectoriesSafe(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = PathSafety.GetRelativePath(source, directory);
                directoriesChecked++;
                ReportCheckProgress(options, $"Preparing folder structure ({directoriesChecked} checked)", relative, directoriesChecked);
                Directory.CreateDirectory(PathSafety.CombineRootAndRelative(destination, relative));
            }
        }

        var sourceFilesChecked = 0;
        foreach (var file in EnumerateFilesSafe(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = PathSafety.GetRelativePath(source, file);
            var target = PathSafety.CombineRootAndRelative(destination, relative);
            sourceFilesChecked++;
            ReportCheckProgress(options, $"Checking source files ({sourceFilesChecked} checked)", relative, sourceFilesChecked);
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);

            if (await ShouldCopyAsync(file, target, options.ComparisonMode, cancellationToken))
            {
                ReportModification(options, relative);
                PathSafety.EnsureInsideRoot(destination, target, "copy");
                File.Copy(file, target, overwrite: true);
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
                copied++;
            }
        }

        var deleted = 0;
        if (mirror)
        {
            var destinationFilesChecked = 0;
            foreach (var destinationFile in EnumerateFilesSafe(destination))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = PathSafety.GetRelativePath(destination, destinationFile);
                destinationFilesChecked++;
                ReportCheckProgress(options, $"Checking destination files ({destinationFilesChecked} checked)", relative, destinationFilesChecked);
                var sourceFile = PathSafety.CombineRootAndRelative(source, relative);
                if (!File.Exists(sourceFile))
                {
                    ReportModification(options, relative, "Deleting");
                    PathSafety.EnsureInsideRoot(destination, destinationFile, "delete");
                    File.Delete(destinationFile);
                    deleted++;
                }
            }

            var destinationDirectoriesChecked = 0;
            foreach (var destinationDirectory in EnumerateDirectoriesSafe(destination).OrderByDescending(path => path.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = PathSafety.GetRelativePath(destination, destinationDirectory);
                destinationDirectoriesChecked++;
                ReportCheckProgress(options, $"Checking destination folders ({destinationDirectoriesChecked} checked)", relative, destinationDirectoriesChecked);
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

        ReportCheckProgress(options, "Native folder synchronization complete", $"{sourceFilesChecked} source file(s) checked");
        await _logService.AppendAsync(options.LogPath, $"Copied/updated: {copied}; deleted: {deleted}", cancellationToken);
    }

    private static async Task<bool> ShouldCopyAsync(
        string sourceFile,
        string destinationFile,
        ComparisonMode comparisonMode,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(destinationFile))
        {
            return true;
        }

        var source = new FileInfo(sourceFile);
        var destination = new FileInfo(destinationFile);
        var metadataMatches = source.Length == destination.Length
            && (source.LastWriteTimeUtc - destination.LastWriteTimeUtc).Duration() <= TimestampTolerance;
        if (comparisonMode == ComparisonMode.Fast)
        {
            return !metadataMatches;
        }

        if (comparisonMode == ComparisonMode.Hybrid && metadataMatches)
        {
            return false;
        }

        await using var sourceStream = File.OpenRead(sourceFile);
        await using var destinationStream = File.OpenRead(destinationFile);
        var sourceHashTask = SHA256.HashDataAsync(sourceStream, cancellationToken).AsTask();
        var destinationHashTask = SHA256.HashDataAsync(destinationStream, cancellationToken).AsTask();
        await Task.WhenAll(sourceHashTask, destinationHashTask);
        return !sourceHashTask.Result.SequenceEqual(destinationHashTask.Result);
    }

    private static void ReportModification(SyncBackendOptions options, string relativePath, string? phase = null)
    {
        var detail = string.IsNullOrWhiteSpace(options.ProgressPathPrefix)
            ? relativePath
            : $"{options.ProgressPathPrefix.TrimEnd('/', '\\')}/{relativePath}";
        options.Progress?.Report(new SyncProgress
        {
            Kind = SyncProgressKind.Modification,
            Phase = phase ?? options.ProgressPhase,
            Detail = detail
        });
    }

    private static void ReportCheckProgress(
        SyncBackendOptions options,
        string phase,
        string relativePath,
        int checkedCount = 0)
    {
        if (checkedCount > 1 && checkedCount % ProgressReportInterval != 0)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(options.ProgressPathPrefix)
            ? relativePath
            : string.IsNullOrWhiteSpace(relativePath)
                ? options.ProgressPathPrefix
                : $"{options.ProgressPathPrefix.TrimEnd('/', '\\')}/{relativePath}";
        options.Progress?.Report(new SyncProgress
        {
            Phase = phase,
            Detail = detail
        });
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
