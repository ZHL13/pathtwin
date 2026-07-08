using System.Diagnostics;
using PathTwin.App.Logging;

namespace PathTwin.App.Backends;

public sealed class RcloneBackend : ISyncBackend
{
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

        return output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.TrimEnd('/').Replace('\\', '/'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    public Task CopyAsync(
        string source,
        string destination,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "copy", source, destination };
        if (options.CreateEmptyDirectories)
        {
            arguments.Add("--create-empty-src-dirs");
        }

        arguments.AddRange(["--log-file", options.LogPath, "--log-level", "INFO"]);
        return RunLoggedAsync(arguments, options.LogPath, cancellationToken);
    }

    public Task SyncAsync(
        string source,
        string destination,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "sync", source, destination };
        if (options.CreateEmptyDirectories)
        {
            arguments.Add("--create-empty-src-dirs");
        }

        arguments.AddRange(["--log-file", options.LogPath, "--log-level", "INFO"]);
        return RunLoggedAsync(arguments, options.LogPath, cancellationToken);
    }

    public Task DryRunAsync(
        string source,
        string destination,
        SyncBackendOptions options,
        CancellationToken cancellationToken = default)
        => RunLoggedAsync(
            ["sync", source, destination, "--dry-run", "--create-empty-src-dirs", "--log-file", options.LogPath, "--log-level", "INFO"],
            options.LogPath,
            cancellationToken);

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
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new FileNotFoundException("Configured rclone executable was not found.", _rclonePath);
        }

        if (!string.IsNullOrWhiteSpace(logPath))
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

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!string.IsNullOrWhiteSpace(logPath))
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

    private static string Quote(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
    }
}
