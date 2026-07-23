using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PathTwin.App.Logging;
using PathTwin.App.Models;
using PathTwin.App.Services;

namespace PathTwin.App.Backends;

public sealed class RcloneBackend : ISyncBackend, IRemoteFileScanBackend
{
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);
    private const int ProgressReportInterval = 128;
    private readonly string _rclonePath;
    private readonly LogService _logService;

    public RcloneBackend(string rclonePath, LogService logService)
    {
        _rclonePath = rclonePath;
        _logService = logService;
    }

    public string Name => "rclone";
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_rclonePath) && File.Exists(_rclonePath);

    public async Task<IReadOnlyList<string>> ListDirectoriesAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(
            ["lsf", root, "--dirs-only", "--recursive"],
            logPath: string.Empty,
            cancellationToken);

        return SplitRcloneLines(output)
            .Select(line => line.TrimEnd('/').Replace('\\', '/'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    public Task CopyAsync(
        string source,
        string destination,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default)
        => TransferAsync(source, destination, mirror: false, options, cancellationToken);

    public Task SyncAsync(
        string source,
        string destination,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default)
        => TransferAsync(source, destination, mirror: true, options, cancellationToken);

    public Task CopyFileAsync(
        string sourceFile,
        string destinationFile,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default)
        => RunLoggedAsync(
            ["copyto", sourceFile, destinationFile, "--log-file", options.LogPath, "--log-level", "INFO"],
            options.LogPath,
            cancellationToken);

    public Task DeleteFileAsync(
        string destinationFile,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default)
        => RunLoggedAsync(
            ["deletefile", destinationFile, "--log-file", options.LogPath, "--log-level", "INFO"],
            options.LogPath,
            cancellationToken);

    public async Task<IReadOnlyList<FileState>> ScanFilesAsync(
        string root,
        IReadOnlyCollection<string> selectedRelativePaths,
        bool includeHashes,
        string logPath,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var scanRoots = selectedRelativePaths.Count == 0
            ? new[] { string.Empty }
            : GetTopLevelPaths(selectedRelativePaths);
        var files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;

        foreach (var selectedPath in scanRoots)
        {
            var normalizedSelection = PathSafety.NormalizeRelativePath(selectedPath);
            var scanRoot = PathSafety.CombineRootAndRelative(root, normalizedSelection);
            var arguments = new List<string> { "lsjson", scanRoot, "--recursive", "--files-only" };
            if (includeHashes)
            {
                arguments.Add("--hash");
            }

            var output = await RunAsync(arguments, logPath: string.Empty, cancellationToken);
            var entries = JsonSerializer.Deserialize<List<RcloneFileEntry>>(output) ?? [];
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDir || string.IsNullOrWhiteSpace(entry.Path))
                {
                    continue;
                }

                var relative = string.IsNullOrWhiteSpace(normalizedSelection)
                    ? PathSafety.NormalizeRelativePath(entry.Path)
                    : $"{normalizedSelection}/{PathSafety.NormalizeRelativePath(entry.Path)}";
                if (string.IsNullOrWhiteSpace(relative))
                {
                    continue;
                }

                scanned++;
                files[relative] = new FileState
                {
                    RelativePath = relative,
                    Size = entry.Size,
                    LastWriteTimeUtc = entry.ModTime.ToUniversalTime(),
                    Sha256 = entry.Hashes is not null && entry.Hashes.TryGetValue("sha256", out var sha256)
                        ? sha256
                        : null
                };
                progress?.Report(new SyncProgress
                {
                    Kind = SyncProgressKind.Comparison,
                    Phase = $"Comparing remote ({scanned} scanned)",
                    Detail = relative
                });
            }
        }

        progress?.Report(new SyncProgress
        {
            Kind = SyncProgressKind.Comparison,
            Phase = $"Comparing remote ({scanned} scanned)",
            Detail = "Scan complete"
        });
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            await _logService.AppendAsync(logPath, $"rclone metadata scan complete: {scanned} file(s).", cancellationToken);
        }
        return files.Values.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task DryRunAsync(
        string source,
        string destination,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "sync", source, destination, "--dry-run", "--create-empty-src-dirs" };
        AddComparisonArguments(arguments, options);
        arguments.AddRange(["--log-file", options.LogPath, "--log-level", "INFO"]);
        return RunLoggedAsync(arguments, options.LogPath, cancellationToken);
    }

    private async Task TransferAsync(
        string source,
        string destination,
        bool mirror,
        SyncBackendOptions options,
        CancellationToken cancellationToken)
    {
        if (options.ComparisonMode == ComparisonMode.Hybrid)
        {
            await TransferHybridWithRcloneAsync(source, destination, mirror, options, cancellationToken);
            return;
        }

        var arguments = new List<string> { mirror ? "sync" : "copy", source, destination };
        if (options.CreateEmptyDirectories)
        {
            arguments.Add("--create-empty-src-dirs");
        }

        AddComparisonArguments(arguments, options);
        arguments.AddRange(["--log-file", options.LogPath, "--log-level", "INFO"]);
        await RunLoggedAsync(arguments, options.LogPath, cancellationToken);
    }

    private async Task TransferHybridWithRcloneAsync(
        string source,
        string destination,
        bool mirror,
        SyncBackendOptions options,
        CancellationToken cancellationToken)
    {
        await EnsureHybridDestinationAsync(destination, options, cancellationToken);
        await _logService.AppendAsync(options.LogPath, "rclone Hybrid transfer: listing both sides by metadata before checksum verification.", cancellationToken);
        ReportHybridProgress(options, "Listing rclone Hybrid source metadata", "Starting");
        var sourceFiles = await ScanFilesAsync(
            source,
            [],
            includeHashes: false,
            logPath: options.LogPath,
            progress: options.Progress,
            cancellationToken: cancellationToken);
        ReportHybridProgress(options, "Listing rclone Hybrid destination metadata", "Starting");
        var destinationFiles = await ScanFilesAsync(
            destination,
            [],
            includeHashes: false,
            logPath: options.LogPath,
            progress: options.Progress,
            cancellationToken: cancellationToken);

        var destinationByPath = destinationFiles.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var filesToVerifyOrCopy = new List<string>();
        var metadataMismatches = 0;
        foreach (var sourceState in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!destinationByPath.TryGetValue(sourceState.RelativePath, out var destinationState)
                || !HasMatchingMetadata(sourceState, destinationState))
            {
                filesToVerifyOrCopy.Add(sourceState.RelativePath);
                metadataMismatches++;
            }
        }

        var sourcePaths = new HashSet<string>(sourceFiles.Select(file => file.RelativePath), StringComparer.OrdinalIgnoreCase);
        var filesToDelete = mirror
            ? destinationFiles
                .Where(file => !sourcePaths.Contains(file.RelativePath))
                .Select(file => file.RelativePath)
                .OrderBy(relativePath => relativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        ReportHybridProgress(
            options,
            "Verifying rclone Hybrid metadata mismatches by checksum",
            $"{metadataMismatches} file(s) require checksum verification");

        await RunWithFileListAsync(
            "copy",
            source,
            destination,
            filesToVerifyOrCopy,
            options,
            checksum: true,
            cancellationToken: cancellationToken);

        if (filesToDelete.Length > 0)
        {
            await RunWithFileListAsync(
                "delete",
                destination,
                destination: null,
                filesToDelete,
                options,
                checksum: false,
                cancellationToken: cancellationToken);
        }

        if (options.CreateEmptyDirectories)
        {
            await ReconcileHybridDirectoriesAsync(source, destination, mirror, options, cancellationToken);
        }

        ReportHybridProgress(
            options,
            "rclone Hybrid transfer complete",
            $"{sourceFiles.Count} source file(s), {metadataMismatches} checksum candidate(s)");
        await _logService.AppendAsync(
            options.LogPath,
            $"rclone Hybrid transfer summary: source={sourceFiles.Count}, checksum candidates={metadataMismatches}, deleted={filesToDelete.Length}",
            cancellationToken);
    }

    private async Task RunWithFileListAsync(
        string command,
        string source,
        string? destination,
        IReadOnlyCollection<string> relativePaths,
        SyncBackendOptions options,
        bool checksum,
        CancellationToken cancellationToken)
    {
        if (relativePaths.Count == 0)
        {
            return;
        }

        var fileListPath = Path.Combine(Path.GetTempPath(), $"PathTwin-rclone-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllLinesAsync(fileListPath, relativePaths, cancellationToken);
            var arguments = new List<string> { command, source };
            if (!string.IsNullOrWhiteSpace(destination))
            {
                arguments.Add(destination);
            }

            arguments.AddRange(["--files-from-raw", fileListPath]);
            if (command == "copy")
            {
                arguments.Add("--no-traverse");
            }

            if (checksum)
            {
                arguments.Add("--checksum");
            }

            arguments.AddRange(["--stats", "1s", "--stats-one-line", "--stats-log-level", "NOTICE", "--log-level", "INFO"]);

            var phase = checksum
                ? "Verifying rclone Hybrid metadata mismatches by checksum"
                : "Removing rclone Hybrid mirror-only files";
            var outputLineCount = 0;
            var modifiedFiles = new ConcurrentQueue<string>();
            using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeatTask = ReportRcloneFileListHeartbeatAsync(
                options,
                phase,
                relativePaths.Count,
                heartbeatCancellation.Token);
            try
            {
                await RunAsync(
                    arguments,
                    options.LogPath,
                    cancellationToken,
                    line => ReportRcloneFileListOutput(
                        options,
                        phase,
                        line,
                        Interlocked.Increment(ref outputLineCount)),
                    logExecution: false,
                    logOutput: false,
                    modificationLineHandler: modifiedFiles.Enqueue);
            }
            finally
            {
                heartbeatCancellation.Cancel();
                try
                {
                    await heartbeatTask;
                }
                catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested)
                {
                    // The heartbeat is expected to end with the rclone command.
                }
            }

            foreach (var line in modifiedFiles)
            {
                await _logService.AppendAsync(options.LogPath, line, cancellationToken);
            }
        }
        finally
        {
            try
            {
                File.Delete(fileListPath);
            }
            catch (IOException)
            {
                // The temporary manifest is harmless if another process still holds it.
            }
        }
    }

    private static bool HasMatchingMetadata(FileState source, FileState destination)
        => source.Size == destination.Size
            && (source.LastWriteTimeUtc - destination.LastWriteTimeUtc).Duration() <= TimestampTolerance;

    private Task EnsureHybridDestinationAsync(
        string destination,
        SyncBackendOptions options,
        CancellationToken cancellationToken)
        => RunLoggedAsync(
            ["mkdir", destination, "--log-file", options.LogPath, "--log-level", "INFO"],
            options.LogPath,
            cancellationToken);

    private async Task ReconcileHybridDirectoriesAsync(
        string source,
        string destination,
        bool mirror,
        SyncBackendOptions options,
        CancellationToken cancellationToken)
    {
        var output = await RunAsync(
            ["lsf", source, "--dirs-only", "--recursive"],
            logPath: string.Empty,
            cancellationToken);
        var directories = SplitRcloneLines(output)
            .Select(path => path.TrimEnd('/', '\\'))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ReportHybridProgress(options, "Creating rclone Hybrid empty directories", $"{directories.Length} directory path(s)");
        if (Path.IsPathFullyQualified(destination))
        {
            await CreateFileSystemDirectoriesAsync(destination, directories, options, cancellationToken);
        }
        else
        {
            await CreateRcloneDirectoriesAsync(destination, directories, options, cancellationToken);
        }

        if (mirror)
        {
            ReportHybridProgress(options, "Cleaning rclone Hybrid mirror empty directories", "Starting");
            await RunLoggedAsync(
                ["rmdirs", destination, "--leave-root", "--log-file", options.LogPath, "--log-level", "INFO"],
                options.LogPath,
                cancellationToken);
        }
    }

    private static Task CreateFileSystemDirectoriesAsync(
        string destination,
        IReadOnlyList<string> directories,
        SyncBackendOptions options,
        CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            for (var index = 0; index < directories.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(PathSafety.CombineRootAndRelative(destination, directories[index]));
                if (index == 0 || (index + 1) % ProgressReportInterval == 0 || index == directories.Count - 1)
                {
                    ReportHybridProgress(
                        options,
                        "Creating rclone Hybrid empty directories",
                        $"{index + 1}/{directories.Count}: {directories[index]}");
                }
            }
        }, cancellationToken);

    private async Task CreateRcloneDirectoriesAsync(
        string destination,
        IReadOnlyList<string> directories,
        SyncBackendOptions options,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < directories.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunLoggedAsync(
                ["mkdir", CombineRclonePath(destination, directories[index]), "--log-file", options.LogPath, "--log-level", "INFO"],
                options.LogPath,
                cancellationToken);
            if (index == 0 || (index + 1) % ProgressReportInterval == 0 || index == directories.Count - 1)
            {
                ReportHybridProgress(
                    options,
                    "Creating rclone Hybrid empty directories",
                    $"{index + 1}/{directories.Count}: {directories[index]}");
            }
        }
    }

    private static string CombineRclonePath(string root, string relativePath)
    {
        if (Path.IsPathFullyQualified(root))
        {
            return PathSafety.CombineRootAndRelative(root, relativePath);
        }

        return $"{root.TrimEnd('/')}/{relativePath.TrimStart('/')}";
    }

    private static IEnumerable<string> SplitRcloneLines(string output)
        => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task ReportRcloneFileListHeartbeatAsync(
        SyncBackendOptions options,
        string phase,
        int candidateCount,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            options.Progress?.Report(new SyncProgress
            {
                Phase = phase,
                Detail = WithProgressPathPrefix(options, $"rclone is checking {candidateCount} candidate file(s)"),
                Completed = 0,
                Total = candidateCount
            });
        }
    }

    private static void ReportRcloneFileListOutput(
        SyncBackendOptions options,
        string phase,
        string line,
        int outputLineCount)
    {
        var detail = line.Trim();
        if (string.IsNullOrWhiteSpace(detail))
        {
            return;
        }

        if (IsRcloneStatusLine(detail))
        {
            options.Progress?.Report(new SyncProgress
            {
                Phase = phase,
                Detail = WithProgressPathPrefix(options, detail)
            });
            return;
        }

        if (outputLineCount > 8 && outputLineCount % ProgressReportInterval != 0)
        {
            return;
        }

        options.Progress?.Report(new SyncProgress
        {
            Kind = SyncProgressKind.Modification,
            Phase = phase,
            Detail = WithProgressPathPrefix(options, GetRcloneFileProgressPath(detail))
        });
    }

    private static string GetRcloneFileProgressPath(string line)
    {
        const string logPrefixSeparator = " : ";
        var prefixIndex = line.IndexOf(logPrefixSeparator, StringComparison.Ordinal);
        var message = prefixIndex < 0
            ? line
            : line[(prefixIndex + logPrefixSeparator.Length)..];
        var actionIndex = message.LastIndexOf(": ", StringComparison.Ordinal);
        return actionIndex <= 0 ? message : message[..actionIndex];
    }

    private static bool IsRcloneStatusLine(string line)
        => line.Contains("Transferred:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Checks:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Elapsed time:", StringComparison.OrdinalIgnoreCase);

    private static string WithProgressPathPrefix(SyncBackendOptions options, string detail)
        => string.IsNullOrWhiteSpace(options.ProgressPathPrefix)
            ? detail
            : string.IsNullOrWhiteSpace(detail)
                ? options.ProgressPathPrefix
                : $"{options.ProgressPathPrefix.TrimEnd('/', '\\')}/{detail}";

    private static void ReportHybridProgress(
        SyncBackendOptions options,
        string phase,
        string detail,
        int checkedCount = 0)
    {
        if (checkedCount > 1 && checkedCount % ProgressReportInterval != 0)
        {
            return;
        }

        options.Progress?.Report(new SyncProgress
        {
            Phase = phase,
            Detail = WithProgressPathPrefix(options, detail)
        });
    }

    private async Task RunLoggedAsync(
        IReadOnlyList<string> arguments,
        string logPath,
        CancellationToken cancellationToken)
    {
        await RunAsync(arguments, logPath, cancellationToken);
    }

    private async Task<string> RunAsync(
        IReadOnlyList<string> arguments,
        string logPath,
        CancellationToken cancellationToken,
        Action<string>? outputLineHandler = null,
        bool logExecution = true,
        bool logOutput = true,
        Action<string>? modificationLineHandler = null)
    {
        if (!IsAvailable)
        {
            throw new FileNotFoundException("Configured rclone executable was not found.", _rclonePath);
        }

        if (!string.IsNullOrWhiteSpace(logPath) && logExecution)
        {
            await _logService.AppendAsync(logPath, $"Backend: {Name}", cancellationToken);
            await _logService.AppendAsync(logPath, $"Command: {Quote(_rclonePath)} {string.Join(' ', arguments.Select(Quote))}", cancellationToken);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _rclonePath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start rclone process.");

        var stdoutBuffer = new StringBuilder();
        var stderrBuffer = new StringBuilder();
        var stdoutTask = ReadProcessOutputAsync(
            process.StandardOutput,
            stdoutBuffer,
            outputLineHandler,
            modificationLineHandler,
            cancellationToken);
        var stderrTask = ReadProcessOutputAsync(
            process.StandardError,
            stderrBuffer,
            outputLineHandler,
            modificationLineHandler,
            cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);
        var stdout = stdoutBuffer.ToString();
        var stderr = stderrBuffer.ToString();

        if (!string.IsNullOrWhiteSpace(logPath) && logOutput)
        {
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                await _logService.AppendAsync(logPath, stdout.TrimEnd(), cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                await _logService.AppendAsync(logPath, stderr.TrimEnd(), cancellationToken);
            }
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"rclone exited with code {process.ExitCode}. {stderr}");
        }

        return stdout;
    }

    private static async Task ReadProcessOutputAsync(
        StreamReader reader,
        StringBuilder buffer,
        Action<string>? outputLineHandler,
        Action<string>? modificationLineHandler,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (buffer.Length > 0)
            {
                buffer.AppendLine();
            }

            buffer.Append(line);
            outputLineHandler?.Invoke(line);
            if (IsRcloneFileModificationLine(line))
            {
                modificationLineHandler?.Invoke(line.Trim());
            }
        }
    }

    private static bool IsRcloneFileModificationLine(string line)
        => line.Contains(": Copied", StringComparison.OrdinalIgnoreCase)
            || line.Contains(": Deleted", StringComparison.OrdinalIgnoreCase)
            || line.Contains(": Moved", StringComparison.OrdinalIgnoreCase)
            || line.Contains(": Renamed", StringComparison.OrdinalIgnoreCase);

    private static string Quote(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
    }

    private static void AddComparisonArguments(List<string> arguments, SyncBackendOptions options)
    {
        if (options.ComparisonMode == ComparisonMode.Content)
        {
            arguments.Add("--checksum");
        }
    }

    private static IReadOnlyList<string> GetTopLevelPaths(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var path in paths
            .Select(PathSafety.NormalizeRelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!result.Any(existing =>
                path.Equals(existing, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(existing + "/", StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(path);
            }
        }

        return result;
    }

    private sealed class RcloneFileEntry
    {
        public string Path { get; init; } = string.Empty;
        public long Size { get; init; }
        public DateTimeOffset ModTime { get; init; }
        public bool IsDir { get; init; }
        public Dictionary<string, string>? Hashes { get; init; }
    }
}
