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
    private readonly SessionStatusService _sessionStatusService;
    private readonly StartWorkCleanupService _startWorkCleanupService;

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
        _sessionStatusService = new SessionStatusService();
        _startWorkCleanupService = new StartWorkCleanupService(logService, new RecycleBinService());
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
        IProgress<SyncProgress>? progress = null,
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
            Status = SessionStatusService.Active,
            StartedAt = DateTimeOffset.Now,
            RemoteRoot = profile.RemoteRoot,
            LocalRoot = profile.LocalRoot,
            HistoryRoot = profile.HistoryRoot,
            LogRoot = profile.LogRoot,
            SkeletonDepth = profile.SkeletonDepth,
            InitialSelectedPaths = [.. normalized],
            SelectedPaths = normalized,
            PreserveDirectorySkeleton = profile.PreserveDirectorySkeleton,
            PullLogPath = Path.Combine(profile.LogRoot, $"{sessionId}_pull.log"),
            PushLogPath = Path.Combine(profile.LogRoot, $"{sessionId}_push.log"),
            AppLogPath = Path.Combine(profile.LogRoot, $"{sessionId}_app.log")
        };
        session.Events.Add(CreateSessionEvent("StartWork", normalized, session.PullLogPath));

        await _logService.WriteSessionHeaderAsync(session.PullLogPath, session, "Start Work Session / Pull", cancellationToken);
        await _logService.WriteSessionHeaderAsync(session.AppLogPath, session, "App", cancellationToken);

        session.Manifest = new SessionManifest
        {
            SessionId = session.SessionId,
            Status = session.Status,
            CapturedAt = DateTimeOffset.Now,
            SkeletonDepth = session.SkeletonDepth,
            InitialSelectedPaths = [.. session.InitialSelectedPaths],
            SelectedPaths = [.. session.SelectedPaths],
            Events = [.. session.Events],
            InitialPullLogPath = session.PullLogPath
        };

        var previousSession = await _sessionStatusService.GetLatestPreviousSessionAsync(profile.LocalRoot, cancellationToken);
        if (!SessionStatusService.IsPreviousSessionSafeToClean(previousSession))
        {
            await LogStartWorkCleanupBlockedAsync(session, previousSession, cancellationToken);
            throw new InvalidOperationException("Previous session was not completed successfully. Local files may contain unpushed changes. Please recover or finish the previous session before starting a new one.");
        }

        RefreshManifestSessionMetadata(session);
        await SaveSessionAsync(session, cancellationToken);

        try
        {
            progress?.Report(new SyncProgress { Phase = "Synchronizing remote for manifest", Detail = $"{normalized.Count} folder(s)", Completed = 0, Total = 0 });
            session.Manifest.RemoteFilesAtPull = [.. await _fileScanner.ScanAsync(profile.RemoteRoot, normalized, computeHashes: true, progress, cancellationToken)];

            IReadOnlyList<string> skeletonPaths = [];
            if (session.PreserveDirectorySkeleton)
            {
                progress?.Report(new SyncProgress { Phase = "Comparing directory skeleton", Detail = $"Enumerating remote directories to depth {session.SkeletonDepth}...", Completed = 0, Total = 0 });
                skeletonPaths = await Task.Run(() => GetDirectorySkeletonPaths(profile.RemoteRoot, session.SkeletonDepth, cancellationToken), cancellationToken);
            }

            progress?.Report(new SyncProgress { Phase = "Cleaning unselected local content", Detail = "Sending stale cache items to Recycle Bin", Completed = 0, Total = 0 });
            var cleanupPlan = await _startWorkCleanupService.BuildCleanupPlanAsync(
                profile.LocalRoot,
                normalized,
                skeletonPaths,
                cancellationToken);
            await LogStartWorkCleanupPlanAsync(session, previousSession, cleanupPlan, cancellationToken);
            await _startWorkCleanupService.ExecuteCleanupPlanAsync(cleanupPlan, session.AppLogPath, cancellationToken);

            if (session.PreserveDirectorySkeleton)
            {
                progress?.Report(new SyncProgress { Phase = "Synchronizing directory skeleton", Detail = $"Creating missing directories to depth {session.SkeletonDepth}...", Completed = 0, Total = 0 });
                await CreateDirectorySkeletonAsync(profile.LocalRoot, skeletonPaths, session.SkeletonDepth, session.AppLogPath, progress, cancellationToken);
            }

            var backend = _backendFactory.Create(profile);
            await _logService.AppendAsync(session.PullLogPath, $"Pull backend: {backend.Name}", cancellationToken);
            await PullFoldersAsync(profile, backend, normalized, session.PullLogPath, "Pulling folder", progress, cancellationToken);
        }
        catch (Exception exception)
        {
            await MarkSessionFailedAsync(session, exception);
            throw;
        }

        RefreshManifestSessionMetadata(session);
        await SaveSessionAsync(session, cancellationToken);
        await _logService.AppendAsync(session.AppLogPath, "Session manifest saved.", cancellationToken);
        return new WorkSessionStartResult
        {
            Session = session,
            Message = $"Session {session.SessionId} started."
        };
    }

    public async Task<WorkSessionResumeResult> ResumeWithAdditionalFoldersAsync(
        WorkSession session,
        ProfileConfig profile,
        IReadOnlyCollection<string> selectedPaths,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        PrepareProfileDefaults(profile);
        ApplySessionScopeToProfile(session, profile);
        ValidateProfile(profile, requireRemote: true);
        Directory.CreateDirectory(session.LocalRoot);
        Directory.CreateDirectory(session.LogRoot);

        var requested = NormalizeSelectedPaths(selectedPaths);
        var existingSet = new HashSet<string>(session.SelectedPaths, StringComparer.OrdinalIgnoreCase);
        var added = requested
            .Where(path => !IsUnderAnySelectedPath(path, existingSet))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (added.Count == 0)
        {
            return new WorkSessionResumeResult
            {
                Session = session,
                AddedPaths = [],
                Message = "No new folders selected."
            };
        }

        var resumeLogPath = Path.Combine(session.LogRoot, $"{session.SessionId}_resume_{DateTime.Now:HHmmss}.log");
        session.Events.Add(CreateSessionEvent("AddFolder", added, resumeLogPath));
        session.Events.Add(CreateSessionEvent("ResumeSync", added, resumeLogPath));
        session.AddedPullLogPaths.Add(resumeLogPath);
        session.AddedSelectedPaths = NormalizeSelectedPaths(session.AddedSelectedPaths.Concat(added).ToArray());
        session.SelectedPaths = NormalizeSelectedPaths(session.SelectedPaths.Concat(added).ToArray());

        await _logService.WriteSessionHeaderAsync(resumeLogPath, session, "Resume Sync / Pull Added Folders", cancellationToken);

        progress?.Report(new SyncProgress { Phase = "Synchronizing added remote folders for manifest", Detail = $"{added.Count} folder(s)", Completed = 0, Total = 0 });
        var addedBaseFiles = await _fileScanner.ScanAsync(session.RemoteRoot, added, computeHashes: true, progress, cancellationToken);
        session.Manifest.RemoteFilesAtPull = MergeFileStates(session.Manifest.RemoteFilesAtPull, addedBaseFiles);

        if (session.PreserveDirectorySkeleton)
        {
            progress?.Report(new SyncProgress { Phase = "Synchronizing directory skeleton", Detail = $"Enumerating remote directories to depth {session.SkeletonDepth}...", Completed = 0, Total = 0 });
            var skeletonPaths = await Task.Run(() => GetDirectorySkeletonPaths(profile.RemoteRoot, session.SkeletonDepth, cancellationToken), cancellationToken);
            await CreateDirectorySkeletonAsync(profile.LocalRoot, skeletonPaths, session.SkeletonDepth, session.AppLogPath, progress, cancellationToken);
        }

        var backend = _backendFactory.Create(profile);
        await _logService.AppendAsync(resumeLogPath, $"Pull backend: {backend.Name}", cancellationToken);
        await PullFoldersAsync(profile, backend, added, resumeLogPath, "Pulling added folder", progress, cancellationToken);

        RefreshManifestSessionMetadata(session);
        await SaveSessionAsync(session, cancellationToken);
        await _logService.AppendAsync(session.AppLogPath, $"Resume Sync completed. Added folders: {string.Join(", ", added)}", cancellationToken);

        return new WorkSessionResumeResult
        {
            Session = session,
            AddedPaths = added,
            Message = $"Added {added.Count} folder(s) to session {session.SessionId}."
        };
    }

    public async Task<WorkSessionEndResult> EndAsync(
        WorkSession session,
        ProfileConfig profile,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        PrepareProfileDefaults(profile);
        Directory.CreateDirectory(session.LogRoot);
        Directory.CreateDirectory(session.HistoryRoot);
        await _logService.WriteSessionHeaderAsync(session.PushLogPath, session, "End Work Session / Push", cancellationToken);

        session.SelectedPaths = NormalizeSelectedPaths(session.SelectedPaths);
        var baseFiles = DeduplicateFileStates(session.Manifest.RemoteFilesAtPull);

        progress?.Report(new SyncProgress { Phase = "Synchronizing selected local folders", Detail = "", Completed = 0, Total = 0 });
        var localFiles = await _fileScanner.ScanAsync(session.LocalRoot, session.SelectedPaths, computeHashes: true, progress, cancellationToken);

        progress?.Report(new SyncProgress { Phase = "Synchronizing selected remote folders", Detail = "", Completed = 0, Total = 0 });
        var remoteFiles = await _fileScanner.ScanAsync(session.RemoteRoot, session.SelectedPaths, computeHashes: true, progress, cancellationToken);

        progress?.Report(new SyncProgress { Phase = "Building sync plan", Detail = $"Synchronizing {localFiles.Count} local vs {remoteFiles.Count} remote files", Completed = 0, Total = 0 });
        var plan = _planner.CreatePlan(baseFiles, localFiles, remoteFiles);

        progress?.Report(new SyncProgress { Phase = "Categorizing unselected folders", Detail = "", Completed = 0, Total = 0 });
        var unselectedPlan = BuildUnselectedPushPlan(session, progress, cancellationToken);
        await WriteCategorizedPushPlanAsync(session, unselectedPlan, cancellationToken);
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

        var backend = _backendFactory.Create(profile);
        if (backend.Name == "rclone")
        {
            await _executor.BackupRemoteChangesAsync(plan, session, session.HistoryRoot, session.PushLogPath, progress, cancellationToken);
            await MirrorPushSelectedFoldersAsync(backend, session, progress, cancellationToken);
        }
        else
        {
            progress?.Report(new SyncProgress { Phase = "Executing selected-folder mirror push", Detail = $"{plan.UploadCount} uploads, {plan.DeleteCount} deletes, {plan.SkipCount} skipped", Completed = 0, Total = plan.UploadCount + plan.DeleteCount });
            await _executor.ExecuteAsync(plan, session, session.HistoryRoot, session.PushLogPath, progress, cancellationToken);
        }

        await CopyUpdateUnselectedFoldersAsync(backend, session, unselectedPlan.UpdateOnlyPaths, progress, cancellationToken);
        await _historyManager.CleanOldHistoryAsync(session.HistoryRoot, profile.HistoryRetentionDays, session.PushLogPath, cancellationToken);

        session.EndedAt = DateTimeOffset.Now;
        session.Status = SessionStatusService.Completed;
        session.Events.Add(CreateSessionEvent("EndWork", session.SelectedPaths, session.PushLogPath));
        session.Manifest.FinalPushLogPath = session.PushLogPath;
        RefreshManifestSessionMetadata(session);
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

    private async Task LogStartWorkCleanupPlanAsync(
        WorkSession session,
        PreviousSessionStatus? previousSession,
        StartWorkCleanupPlan cleanupPlan,
        CancellationToken cancellationToken)
    {
        await _logService.AppendAsync(session.AppLogPath, "Start Work cleanup:", cancellationToken);
        await _logService.AppendAsync(session.AppLogPath, $"Session: {session.SessionId}", cancellationToken);
        await _logService.AppendAsync(session.AppLogPath, $"Previous session status: {FormatPreviousSessionStatus(previousSession)}", cancellationToken);
        await _logService.AppendAsync(session.AppLogPath, "Mode: send to Windows Recycle Bin", cancellationToken);
        await _logService.AppendAsync(session.AppLogPath, "Selected paths preserved:", cancellationToken);
        foreach (var path in cleanupPlan.SelectedPaths)
        {
            await _logService.AppendAsync(session.AppLogPath, $"- {path}", cancellationToken);
        }

        await _logService.AppendAsync(session.AppLogPath, $"Preserved skeleton directories: {cleanupPlan.PreservedSkeletonPaths.Count}", cancellationToken);
        await _logService.AppendAsync(session.AppLogPath, "Cleaned unselected local content:", cancellationToken);
        if (cleanupPlan.Items.Count == 0)
        {
            await _logService.AppendAsync(session.AppLogPath, "- (none)", cancellationToken);
            return;
        }

        foreach (var item in cleanupPlan.Items)
        {
            await _logService.AppendAsync(session.AppLogPath, $"- {item.RelativePath}", cancellationToken);
        }
    }

    private async Task LogStartWorkCleanupBlockedAsync(
        WorkSession session,
        PreviousSessionStatus? previousSession,
        CancellationToken cancellationToken)
    {
        await _logService.AppendAsync(session.AppLogPath, "Start Work cleanup blocked:", cancellationToken);
        await _logService.AppendAsync(session.AppLogPath, $"Previous session status: {FormatPreviousSessionStatus(previousSession)}", cancellationToken);
        await _logService.AppendAsync(session.AppLogPath, "No local files were cleaned.", cancellationToken);
    }

    private async Task MarkSessionFailedAsync(WorkSession session, Exception exception)
    {
        session.Status = exception is OperationCanceledException
            ? SessionStatusService.Interrupted
            : SessionStatusService.Failed;
        RefreshManifestSessionMetadata(session);

        try
        {
            await SaveSessionAsync(session, CancellationToken.None);
            await _logService.WriteExceptionAsync(session.AppLogPath, exception, CancellationToken.None);
        }
        catch
        {
            // Preserve the original failure; status persistence is best effort here.
        }
    }

    private static string FormatPreviousSessionStatus(PreviousSessionStatus? previousSession)
        => previousSession is null
            ? "None"
            : $"{previousSession.Status} ({previousSession.SessionId})";

    private async Task CreateDirectorySkeletonAsync(
        string localRoot,
        IReadOnlyList<string> skeletonPaths,
        int skeletonDepth,
        string logPath,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(localRoot);
        var created = 0;
        foreach (var relative in skeletonPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localDirectory = PathSafety.CombineRootAndRelative(localRoot, relative);
            PathSafety.EnsureInsideRoot(localRoot, localDirectory, "create directory skeleton");
            if (!Directory.Exists(localDirectory))
            {
                Directory.CreateDirectory(localDirectory);
                created++;
            }

            if (progress is not null)
            {
                progress.Report(new SyncProgress { Phase = $"Synchronizing skeleton ({created} dirs)", Detail = relative });
            }
        }

        await _logService.AppendAsync(logPath, $"Directory skeleton created/verified. Depth: {skeletonDepth}; created: {created}; planned: {skeletonPaths.Count}", cancellationToken);
    }

    private async Task PullFoldersAsync(
        ProfileConfig profile,
        ISyncBackend backend,
        IReadOnlyList<string> relativePaths,
        string logPath,
        string phase,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var i = 0;
        foreach (var relativePath in relativePaths)
        {
            i++;
            progress?.Report(new SyncProgress { Phase = $"{phase} ({i}/{relativePaths.Count})", Detail = relativePath, Completed = i - 1, Total = relativePaths.Count });
            var source = PathSafety.CombineRootAndRelative(profile.RemoteRoot, relativePath);
            var destination = PathSafety.CombineRootAndRelative(profile.LocalRoot, relativePath);
            var options = new SyncBackendOptions
            {
                LogPath = logPath,
                Mirror = profile.PullMode == PullMode.Mirror,
                CreateEmptyDirectories = true
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
    }

    private async Task MirrorPushSelectedFoldersAsync(
        ISyncBackend backend,
        WorkSession session,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var mirrorRoots = GetTopLevelPaths(session.SelectedPaths);
        var completed = 0;
        foreach (var relativePath in mirrorRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SyncProgress
            {
                Phase = $"Mirror-pushing selected folders ({completed + 1}/{mirrorRoots.Count})",
                Detail = relativePath,
                Completed = completed,
                Total = mirrorRoots.Count
            });

            var source = PathSafety.CombineRootAndRelative(session.LocalRoot, relativePath);
            var destination = PathSafety.CombineRootAndRelative(session.RemoteRoot, relativePath);
            await backend.SyncAsync(source, destination, new SyncBackendOptions
            {
                LogPath = session.PushLogPath,
                Mirror = true,
                CreateEmptyDirectories = true
            }, cancellationToken);
            completed++;
        }
    }

    private async Task CopyUpdateUnselectedFoldersAsync(
        ISyncBackend backend,
        WorkSession session,
        IReadOnlyList<string> updateOnlyPaths,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var completed = 0;
        foreach (var relativePath in updateOnlyPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SyncProgress
            {
                Phase = $"Update-pushing unselected folders ({completed + 1}/{updateOnlyPaths.Count})",
                Detail = relativePath,
                Completed = completed,
                Total = updateOnlyPaths.Count
            });

            var source = PathSafety.CombineRootAndRelative(session.LocalRoot, relativePath);
            if (!Directory.Exists(source))
            {
                completed++;
                continue;
            }

            var destination = PathSafety.CombineRootAndRelative(session.RemoteRoot, relativePath);
            await backend.CopyAsync(source, destination, new SyncBackendOptions
            {
                LogPath = session.PushLogPath,
                Mirror = false,
                CreateEmptyDirectories = false
            }, cancellationToken);
            completed++;
        }
    }

    private UnselectedPushPlan BuildUnselectedPushPlan(
        WorkSession session,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var selectedSet = new HashSet<string>(session.SelectedPaths, StringComparer.OrdinalIgnoreCase);
        var allUnselectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoriesWithFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updateRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(session.LocalRoot))
        {
            return new UnselectedPushPlan([], 0);
        }

        foreach (var directory in EnumerateDirectoriesSafe(session.LocalRoot, recurse: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = PathSafety.GetRelativePath(session.LocalRoot, directory);
            if (IsInsideMetadataFolder(relative) || IsUnderAnySelectedPath(relative, selectedSet))
            {
                continue;
            }

            allUnselectedDirectories.Add(relative);
        }

        var filesSeen = 0;
        foreach (var file in EnumerateFilesSafe(session.LocalRoot, recurse: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = PathSafety.GetRelativePath(session.LocalRoot, file);
            if (IsInsideMetadataFolder(relative))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty;
            if (string.IsNullOrWhiteSpace(directory) || IsUnderAnySelectedPath(directory, selectedSet))
            {
                continue;
            }

            filesSeen++;
            AddAncestorDirectories(directory, directoriesWithFiles);
            var updateRoot = GetUnselectedUpdateRoot(directory, session.SkeletonDepth, selectedSet);
            if (!string.IsNullOrWhiteSpace(updateRoot))
            {
                updateRoots.Add(updateRoot);
            }
        }

        var ignoredEmptyFolders = allUnselectedDirectories.Count(directory => !directoriesWithFiles.Contains(directory));
        progress?.Report(new SyncProgress
        {
            Phase = "Categorized unselected folders",
            Detail = $"{updateRoots.Count} update-only folder(s), {ignoredEmptyFolders} empty skeleton folder(s), {filesSeen} file(s)",
            Completed = 0,
            Total = 0
        });

        return new UnselectedPushPlan(
            updateRoots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            ignoredEmptyFolders);
    }

    private async Task WriteCategorizedPushPlanAsync(
        WorkSession session,
        UnselectedPushPlan unselectedPlan,
        CancellationToken cancellationToken)
    {
        await _logService.AppendAsync(session.PushLogPath, "Categorized push plan:", cancellationToken);
        await _logService.AppendAsync(session.PushLogPath, "Selected folders, mirror push:", cancellationToken);
        foreach (var path in session.SelectedPaths)
        {
            await _logService.AppendAsync(session.PushLogPath, $"* {path}", cancellationToken);
        }

        if (session.SelectedPaths.Count == 0)
        {
            await _logService.AppendAsync(session.PushLogPath, "* (none)", cancellationToken);
        }

        await _logService.AppendAsync(session.PushLogPath, "Unselected non-empty folders, update-only push:", cancellationToken);
        foreach (var path in unselectedPlan.UpdateOnlyPaths)
        {
            await _logService.AppendAsync(session.PushLogPath, $"* {path}", cancellationToken);
        }

        if (unselectedPlan.UpdateOnlyPaths.Count == 0)
        {
            await _logService.AppendAsync(session.PushLogPath, "* (none)", cancellationToken);
        }

        await _logService.AppendAsync(session.PushLogPath, $"Ignored empty skeleton folders: {unselectedPlan.IgnoredEmptyFolderCount} folders", cancellationToken);
    }

    private static IEnumerable<string> EnumerateDirectoriesToDepth(
        string root,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        if (maxDepth <= 0 || !Directory.Exists(root))
        {
            yield break;
        }

        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var directory in EnumerateDirectoriesSafe(current, recurse: false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = PathSafety.GetRelativePath(root, directory);
                if (IsInsideMetadataFolder(relative))
                {
                    continue;
                }

                yield return directory;
                queue.Enqueue((directory, depth + 1));
            }
        }
    }

    private static IReadOnlyList<string> GetDirectorySkeletonPaths(
        string remoteRoot,
        int skeletonDepth,
        CancellationToken cancellationToken)
        => EnumerateDirectoriesToDepth(remoteRoot, Math.Max(0, skeletonDepth), cancellationToken)
            .Select(directory => PathSafety.GetRelativePath(remoteRoot, directory))
            .Where(relative => !IsInsideMetadataFolder(relative))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(relative => relative, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IEnumerable<string> EnumerateDirectoriesSafe(string root, bool recurse)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateDirectories(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = recurse,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).GetEnumerator();
        }
        catch (Exception ex) when (IsEnumerationException(ex))
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string item;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    item = enumerator.Current;
                }
                catch (Exception ex) when (IsEnumerationException(ex))
                {
                    yield break;
                }

                yield return item;
            }
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, bool recurse)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = recurse,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).GetEnumerator();
        }
        catch (Exception ex) when (IsEnumerationException(ex))
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string item;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    item = enumerator.Current;
                }
                catch (Exception ex) when (IsEnumerationException(ex))
                {
                    yield break;
                }

                yield return item;
            }
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

    private static bool IsAncestorOfAnySelectedPath(string relativePath, HashSet<string> selectedSet)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        return selectedSet.Any(selected =>
            selected.Replace('\\', '/').Trim('/').StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetUnselectedUpdateRoot(
        string fileDirectory,
        int skeletonDepth,
        HashSet<string> selectedSet)
    {
        var segments = fileDirectory
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return string.Empty;
        }

        var startDepth = Math.Min(Math.Max(1, skeletonDepth), segments.Length);
        for (var count = startDepth; count <= segments.Length; count++)
        {
            var candidate = string.Join('/', segments.Take(count));
            if (!IsAncestorOfAnySelectedPath(candidate, selectedSet))
            {
                return candidate;
            }
        }

        return fileDirectory.Replace('\\', '/').Trim('/');
    }

    private static void AddAncestorDirectories(string relativeDirectory, HashSet<string> target)
    {
        var segments = relativeDirectory
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 1; i <= segments.Length; i++)
        {
            target.Add(string.Join('/', segments.Take(i)));
        }
    }

    private static bool IsInsideMetadataFolder(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => string.Equals(s, AppConstants.LocalMetadataDirName, StringComparison.OrdinalIgnoreCase));
    }

    private static List<FileState> MergeFileStates(
        IEnumerable<FileState> existing,
        IEnumerable<FileState> added)
    {
        var byPath = existing.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var file in added)
        {
            byPath[file.RelativePath] = file;
        }

        return byPath.Values.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<FileState> DeduplicateFileStates(IEnumerable<FileState> files)
        => MergeFileStates([], files);

    private static SessionEvent CreateSessionEvent(
        string type,
        IReadOnlyCollection<string> paths,
        string logPath)
        => new()
        {
            Type = type,
            Timestamp = DateTimeOffset.Now,
            Paths = [.. paths],
            LogPath = logPath
        };

    private static void RefreshManifestSessionMetadata(WorkSession session)
    {
        session.Manifest.Status = session.Status;
        session.Manifest.SkeletonDepth = session.SkeletonDepth;
        session.Manifest.InitialSelectedPaths = [.. session.InitialSelectedPaths];
        session.Manifest.AddedSelectedPaths = [.. session.AddedSelectedPaths];
        session.Manifest.SelectedPaths = [.. session.SelectedPaths];
        session.Manifest.Events = [.. session.Events];
        session.Manifest.InitialPullLogPath = session.PullLogPath;
        session.Manifest.AddedPullLogPaths = [.. session.AddedPullLogPaths];
        session.Manifest.FinalPushLogPath = session.PushLogPath;
    }

    private static void ApplySessionScopeToProfile(WorkSession session, ProfileConfig profile)
    {
        profile.RemoteRoot = session.RemoteRoot;
        profile.LocalRoot = session.LocalRoot;
        profile.HistoryRoot = session.HistoryRoot;
        profile.LogRoot = session.LogRoot;
        profile.PreserveDirectorySkeleton = session.PreserveDirectorySkeleton;
        profile.SkeletonDepth = session.SkeletonDepth;
    }

    private static IReadOnlyList<string> GetTopLevelPaths(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var path in paths
            .Select(PathSafety.NormalizeRelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
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

    private static bool IsEnumerationException(Exception exception)
        => exception is UnauthorizedAccessException
            or IOException
            or DirectoryNotFoundException
            or PathTooLongException;

    private static void PrepareProfileDefaults(ProfileConfig profile)
    {
        profile.SkeletonDepth = Math.Max(0, profile.SkeletonDepth);

        if (string.IsNullOrWhiteSpace(profile.RclonePath))
        {
            profile.RclonePath = AppConstants.DefaultRclonePath;
        }

        if (!string.IsNullOrWhiteSpace(profile.LocalRoot) && string.IsNullOrWhiteSpace(profile.LogRoot))
        {
            profile.LogRoot = Path.Combine(profile.LocalRoot + AppConstants.LocalMetadataDirName, "logs");
        }

        if (!string.IsNullOrWhiteSpace(profile.LocalRoot) && string.IsNullOrWhiteSpace(profile.HistoryRoot))
        {
            profile.HistoryRoot = Path.Combine(profile.LocalRoot + AppConstants.LocalMetadataDirName, "history");
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

    private sealed record UnselectedPushPlan(
        IReadOnlyList<string> UpdateOnlyPaths,
        int IgnoredEmptyFolderCount);
}
