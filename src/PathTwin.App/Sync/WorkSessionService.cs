using System.Text.Json;
using PathTwin.App.Backends;
using PathTwin.App.Configuration;
using PathTwin.App.Constants;
using PathTwin.App.Logging;
using PathTwin.App.Models;
using PathTwin.App.Services;

namespace PathTwin.App.Sync;

public sealed class WorkSessionService
{
    private readonly ConfigService _configService;
    private readonly LogService _logService;
    private readonly FileScanner _fileScanner;
    private readonly SyncPlanner _planner;
    private readonly SyncBackendFactory _backendFactory;
    private readonly SyncExecutor _executor;
    private readonly VersionHistoryManager _historyManager;

    public WorkSessionService(
        ConfigService configService,
        LogService logService,
        FileScanner fileScanner,
        SyncPlanner planner,
        SyncBackendFactory backendFactory,
        SyncExecutor executor)
    {
        _configService = configService;
        _logService = logService;
        _fileScanner = fileScanner;
        _planner = planner;
        _backendFactory = backendFactory;
        _executor = executor;
        _historyManager = new VersionHistoryManager(logService);
    }

    public async Task<string> DryRunPullAsync(
        ProfileConfig profile,
        IReadOnlyCollection<string> selectedPaths,
        CancellationToken cancellationToken = default)
    {
        PrepareProfileDefaults(profile);
        ValidateProfile(profile, requireRemote: true);
        var normalized = NormalizeSelectedPaths(selectedPaths);
        var sessionId = $"{DateTime.Now:yyyyMMdd-HHmmss}_dryrun";
        var logPath = Path.Combine(profile.LogRoot, $"{sessionId}_pull.log");
        Directory.CreateDirectory(profile.LogRoot);

        var backend = _backendFactory.Create(profile);
        await _logService.AppendAsync(logPath, $"Dry run pull using backend: {backend.Name}", cancellationToken);
        foreach (var relativePath in normalized)
        {
            var source = PathSafety.CombineRootAndRelative(profile.RemoteRoot, relativePath);
            var destination = PathSafety.CombineRootAndRelative(profile.LocalRoot, relativePath);
            await backend.DryRunAsync(source, destination, new SyncBackendOptions
            {
                LogPath = logPath,
                Mirror = profile.PullMode == PullMode.Mirror
            }, cancellationToken);
        }

        return logPath;
    }

    public async Task<WorkSessionStartResult> StartAsync(
        ProfileConfig profile,
        IReadOnlyCollection<string> selectedPaths,
        CancellationToken cancellationToken = default)
    {
        PrepareProfileDefaults(profile);
        ValidateProfile(profile, requireRemote: true);
        var normalized = NormalizeSelectedPaths(selectedPaths);
        if (normalized.Count == 0)
        {
            throw new InvalidOperationException("Select at least one folder before starting a work session.");
        }

        var sessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Directory.CreateDirectory(profile.LocalRoot);
        Directory.CreateDirectory(profile.LogRoot);

        var session = new WorkSession
        {
            SessionId = sessionId,
            StartedAt = DateTimeOffset.Now,
            RemoteRoot = profile.RemoteRoot,
            LocalRoot = profile.LocalRoot,
            HistoryRoot = profile.HistoryRoot,
            LogRoot = profile.LogRoot,
            SelectedPaths = normalized,
            PreserveDirectorySkeleton = profile.PreserveDirectorySkeleton,
            PullLogPath = Path.Combine(profile.LogRoot, $"{sessionId}_pull.log"),
            PushLogPath = Path.Combine(profile.LogRoot, $"{sessionId}_push.log"),
            AppLogPath = Path.Combine(profile.LogRoot, $"{sessionId}_app.log")
        };

        await _logService.WriteSessionHeaderAsync(session.PullLogPath, session, "Start Work Session / Pull", cancellationToken);
        await _logService.WriteSessionHeaderAsync(session.AppLogPath, session, "App", cancellationToken);

        session.Manifest = new SessionManifest
        {
            SessionId = session.SessionId,
            CapturedAt = DateTimeOffset.Now,
            RemoteFilesAtPull = [.. await _fileScanner.ScanAsync(profile.RemoteRoot, normalized, computeHashes: true, cancellationToken)]
        };

        if (profile.PreserveDirectorySkeleton)
        {
            await CreateDirectorySkeletonAsync(profile.RemoteRoot, profile.LocalRoot, session.AppLogPath, cancellationToken);
        }

        var backend = _backendFactory.Create(profile);
        await _logService.AppendAsync(session.PullLogPath, $"Pull backend: {backend.Name}", cancellationToken);
        foreach (var relativePath in normalized)
        {
            var source = PathSafety.CombineRootAndRelative(profile.RemoteRoot, relativePath);
            var destination = PathSafety.CombineRootAndRelative(profile.LocalRoot, relativePath);
            var options = new SyncBackendOptions
            {
                LogPath = session.PullLogPath,
                Mirror = profile.PullMode == PullMode.Mirror
            };

            if (profile.PullMode == PullMode.Mirror)
            {
                await backend.SyncAsync(source, destination, options, cancellationToken);
            }
            else
            {
                await backend.CopyAsync(source, destination, options, cancellationToken);
            }
        }

        await SaveSessionAsync(session, cancellationToken);
        await _logService.AppendAsync(session.AppLogPath, "Session manifest saved.", cancellationToken);
        return new WorkSessionStartResult
        {
            Session = session,
            Message = $"Session {session.SessionId} started."
        };
    }

    public async Task<WorkSessionEndResult> EndAsync(
        WorkSession session,
        ProfileConfig profile,
        CancellationToken cancellationToken = default)
    {
        PrepareProfileDefaults(profile);
        Directory.CreateDirectory(session.LogRoot);
        Directory.CreateDirectory(session.HistoryRoot);
        await _logService.WriteSessionHeaderAsync(session.PushLogPath, session, "End Work Session / Push", cancellationToken);

        var baseFiles = session.Manifest.RemoteFilesAtPull;
        var localFiles = await _fileScanner.ScanAsync(session.LocalRoot, session.SelectedPaths, computeHashes: true, cancellationToken);
        var remoteFiles = await _fileScanner.ScanAsync(session.RemoteRoot, session.SelectedPaths, computeHashes: true, cancellationToken);
        var plan = _planner.CreatePlan(baseFiles, localFiles, remoteFiles);

        // Handle unselected skeleton directories: upload new local files only (no deletions, no change detection)
        await AppendUnselectedUploadsAsync(plan, session, cancellationToken);

        await _logService.WritePlanSummaryAsync(session.PushLogPath, plan, cancellationToken);

        if (plan.Conflicts.Count > 0)
        {
            return new WorkSessionEndResult
            {
                Succeeded = false,
                Message = $"Found {plan.Conflicts.Count} conflict(s). No remote changes were made.",
                Plan = plan,
                LogFolder = session.LogRoot
            };
        }

        await _executor.ExecuteAsync(plan, session, session.HistoryRoot, session.PushLogPath, cancellationToken);
        await _historyManager.CleanOldHistoryAsync(session.HistoryRoot, profile.HistoryRetentionDays, session.PushLogPath, cancellationToken);

        session.EndedAt = DateTimeOffset.Now;
        await SaveSessionAsync(session, cancellationToken);
        await _logService.AppendAsync(session.PushLogPath, "End Work Session completed successfully.", cancellationToken);

        return new WorkSessionEndResult
        {
            Succeeded = true,
            Message = $"Session {session.SessionId} ended successfully.",
            Plan = plan,
            LogFolder = session.LogRoot
        };
    }

    private async Task SaveSessionAsync(WorkSession session, CancellationToken cancellationToken)
    {
        var sessionDirectory = Path.Combine(session.LocalRoot + AppConstants.LocalMetadataDirName, AppConstants.SessionsDirectoryName);
        Directory.CreateDirectory(sessionDirectory);
        var sessionPath = Path.Combine(sessionDirectory, $"{session.SessionId}.json");
        await using var stream = File.Create(sessionPath);
        await JsonSerializer.SerializeAsync(stream, session, ConfigService.SerializerOptions, cancellationToken);
    }

    private async Task CreateDirectorySkeletonAsync(
        string remoteRoot,
        string localRoot,
        string logPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(localRoot);
        var created = 0;
        foreach (var directory in Directory.EnumerateDirectories(remoteRoot, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = PathSafety.GetRelativePath(remoteRoot, directory);

            var segments = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(s => string.Equals(s, AppConstants.LocalMetadataDirName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var localDirectory = PathSafety.CombineRootAndRelative(localRoot, relative);
            PathSafety.EnsureInsideRoot(localRoot, localDirectory, "create directory skeleton");
            Directory.CreateDirectory(localDirectory);
            created++;
        }

        await _logService.AppendAsync(logPath, $"Directory skeleton created/verified. Directories: {created}", cancellationToken);
    }

    private async Task AppendUnselectedUploadsAsync(
        SyncPlan plan,
        WorkSession session,
        CancellationToken cancellationToken)
    {
        var selectedSet = new HashSet<string>(session.SelectedPaths, StringComparer.OrdinalIgnoreCase);
        var unselectedPaths = new List<string>();

        if (Directory.Exists(session.LocalRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(session.LocalRoot, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }))
            {
                var relative = PathSafety.GetRelativePath(session.LocalRoot, directory);
                var segments = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Any(s => string.Equals(s, AppConstants.LocalMetadataDirName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!IsUnderAnySelectedPath(relative, selectedSet))
                {
                    unselectedPaths.Add(relative);
                }
            }
        }

        if (unselectedPaths.Count == 0)
        {
            return;
        }

        var unselectedLocalFiles = await _fileScanner.ScanAsync(session.LocalRoot, unselectedPaths, computeHashes: false, cancellationToken);
        if (unselectedLocalFiles.Count == 0)
        {
            return;
        }

        var unselectedRemoteFiles = await _fileScanner.ScanAsync(session.RemoteRoot, unselectedPaths, computeHashes: false, cancellationToken);
        var remoteSet = new HashSet<string>(
            unselectedRemoteFiles.Select(f => f.RelativePath),
            StringComparer.OrdinalIgnoreCase);

        var uploadedCount = 0;
        foreach (var localFile in unselectedLocalFiles)
        {
            if (!remoteSet.Contains(localFile.RelativePath))
            {
                plan.Operations.Add(new SyncOperation
                {
                    Kind = SyncOperationKind.UploadNew,
                    RelativePath = localFile.RelativePath,
                    Reason = "New local file in unselected skeleton folder.",
                    RequiresRemoteBackup = false
                });
                uploadedCount++;
            }
        }

        if (uploadedCount > 0)
        {
            await _logService.AppendAsync(
                session.PushLogPath,
                $"Unselected folders: {uploadedCount} new file(s) to upload out of {unselectedLocalFiles.Count} found.",
                cancellationToken);
        }
    }

    private static bool IsUnderAnySelectedPath(string relativePath, HashSet<string> selectedSet)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        foreach (var selected in selectedSet)
        {
            var normalizedSelected = selected.Replace('\\', '/').Trim('/');
            if (normalized.Equals(normalizedSelected, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(normalizedSelected + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void PrepareProfileDefaults(ProfileConfig profile)
    {
        if (string.IsNullOrWhiteSpace(profile.RclonePath))
        {
            profile.RclonePath = AppConstants.DefaultRclonePath;
        }

        if (!string.IsNullOrWhiteSpace(profile.LocalRoot) && string.IsNullOrWhiteSpace(profile.LogRoot))
        {
            profile.LogRoot = Path.Combine(profile.LocalRoot + AppConstants.LocalMetadataDirName, "logs");
        }

        if (!string.IsNullOrWhiteSpace(profile.RemoteRoot) && string.IsNullOrWhiteSpace(profile.HistoryRoot))
        {
            profile.HistoryRoot = profile.RemoteRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "_History";
        }
    }

    private static void ValidateProfile(ProfileConfig profile, bool requireRemote)
    {
        if (string.IsNullOrWhiteSpace(profile.RemoteRoot))
        {
            throw new InvalidOperationException("Remote root is required.");
        }

        if (requireRemote && !Directory.Exists(profile.RemoteRoot))
        {
            throw new DirectoryNotFoundException($"Remote root does not exist: {profile.RemoteRoot}");
        }

        if (string.IsNullOrWhiteSpace(profile.LocalRoot))
        {
            throw new InvalidOperationException("Local root is required.");
        }

        if (string.IsNullOrWhiteSpace(profile.HistoryRoot))
        {
            throw new InvalidOperationException("History root is required.");
        }

        if (string.IsNullOrWhiteSpace(profile.LogRoot))
        {
            throw new InvalidOperationException("Log root is required.");
        }
    }

    private static List<string> NormalizeSelectedPaths(IReadOnlyCollection<string> selectedPaths)
        => selectedPaths
            .Select(PathSafety.NormalizeRelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
