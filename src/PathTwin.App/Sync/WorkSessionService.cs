using System.Text.Json;
using PathTwin.App.Backends;
using PathTwin.App.Configuration;
using PathTwin.App.Constants;
using PathTwin.App.Logging;
using PathTwin.App.Models;
using PathTwin.App.Services;
using System.Threading.Channels;

namespace PathTwin.App.Sync;

public sealed class WorkSessionService
{
    private const int MaxConcurrentFolderSyncs = 1;
    private const int MaxConcurrentFileChanges = 1;
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

    public string GetSyncBackendName(ProfileConfig profile)
    {
        PrepareProfileDefaults(profile);
        return _backendFactory.Create(profile).Name;
    }

    public Task<PreviousSessionStatus?> GetLatestPreviousSessionStatusAsync(
        string localRoot,
        CancellationToken cancellationToken = default)
        => _sessionStatusService.GetLatestPreviousSessionAsync(localRoot, cancellationToken);

    private async Task<IReadOnlyList<FileState>> ScanFileStatesAsync(
        ProfileConfig profile,
        string root,
        IReadOnlyCollection<string> selectedPaths,
        string logPath,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var includeHashes = profile.ComparisonMode == ComparisonMode.Content;
        var backend = _backendFactory.Create(profile);
        IReadOnlyList<FileState> states;

        if (string.Equals(root, profile.RemoteRoot, StringComparison.OrdinalIgnoreCase)
            && backend is IRemoteFileScanBackend remoteScanner)
        {
            states = await remoteScanner.ScanFilesAsync(
                root,
                selectedPaths,
                includeHashes,
                logPath,
                progress,
                cancellationToken);
        }
        else
        {
            states = await _fileScanner.ScanAsync(root, selectedPaths, includeHashes, progress, cancellationToken);
        }

        if (!includeHashes)
        {
            return states;
        }

        var completed = new List<FileState>(states.Count);
        foreach (var state in states)
        {
            completed.Add(await _fileScanner.EnsureSha256Async(root, state, cancellationToken));
        }

        return completed;
    }

    public async Task<WorkSessionStartResult> StartAsync(
        ProfileConfig profile,
        IReadOnlyCollection<string> selectedPaths,
        IProgress<SyncProgress>? progress = null,
        bool ignorePreviousSessionStatus = false,
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
            if (!ignorePreviousSessionStatus)
            {
                await LogStartWorkCleanupBlockedAsync(session, previousSession, cancellationToken);
                throw new InvalidOperationException("Previous session was not completed successfully. Local files may contain unpushed changes. Please recover or finish the previous session before starting a new one.");
            }

            await _logService.AppendAsync(
                session.AppLogPath,
                $"Previous session status was explicitly ignored: {FormatPreviousSessionStatus(previousSession)}",
                cancellationToken);
        }

        RefreshManifestSessionMetadata(session);
        await SaveSessionAsync(session, cancellationToken);

        try
        {
            var backend = _backendFactory.Create(profile);
            if (backend is not NativeSyncBackend)
            {
                session.FailurePhase = "Scanning remote files for the new session";
                progress?.Report(new SyncProgress { Phase = "Synchronizing remote for manifest", Detail = $"{normalized.Count} folder(s)", Completed = 0, Total = 0 });
                session.Manifest.RemoteFilesAtPull = [.. await Task.Run(
                    () => ScanFileStatesAsync(profile, profile.RemoteRoot, normalized, session.PullLogPath, progress, cancellationToken),
                    cancellationToken)];
            }

            IReadOnlyList<string> skeletonPaths = [];
            if (session.PreserveDirectorySkeleton)
            {
                session.FailurePhase = "Reading the remote directory skeleton";
                progress?.Report(new SyncProgress { Phase = "Comparing directory skeleton", Detail = $"Enumerating remote directories to depth {session.SkeletonDepth}...", Completed = 0, Total = 0 });
                skeletonPaths = await Task.Run(() => GetDirectorySkeletonPaths(profile.RemoteRoot, session.SkeletonDepth, cancellationToken), cancellationToken);
            }

            session.FailurePhase = "Cleaning unselected local content";
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
                session.FailurePhase = "Creating the local directory skeleton";
                progress?.Report(new SyncProgress { Phase = "Synchronizing directory skeleton", Detail = $"Creating missing directories to depth {session.SkeletonDepth}...", Completed = 0, Total = 0 });
                await CreateDirectorySkeletonAsync(profile.LocalRoot, skeletonPaths, session.SkeletonDepth, session.AppLogPath, progress, cancellationToken);
            }

            session.FailurePhase = "Pulling selected folders";
            await _logService.AppendAsync(session.PullLogPath, $"Pull backend: {backend.Name}", cancellationToken);
            if (backend is NativeSyncBackend)
            {
                session.Manifest.RemoteFilesAtPull = [.. await Task.Run(
                    () => ScanAndPullNativeFoldersAsync(
                        profile,
                        backend,
                        normalized,
                        session.PullLogPath,
                        progress,
                        cancellationToken),
                    cancellationToken)];
            }
            else
            {
                await PullFoldersAsync(profile, backend, normalized, session.PullLogPath, "Pulling folder", progress, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            await MarkSessionFailedAsync(session, exception, session.FailurePhase);
            throw;
        }

        session.FailurePhase = string.Empty;
        session.FailureDetails = string.Empty;
        session.FailedAt = null;
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

        var backend = _backendFactory.Create(profile);
        if (backend is not NativeSyncBackend)
        {
            progress?.Report(new SyncProgress { Phase = "Synchronizing added remote folders for manifest", Detail = $"{added.Count} folder(s)", Completed = 0, Total = 0 });
            var addedBaseFiles = await Task.Run(
                () => ScanFileStatesAsync(profile, session.RemoteRoot, added, resumeLogPath, progress, cancellationToken),
                cancellationToken);
            session.Manifest.RemoteFilesAtPull = MergeFileStates(session.Manifest.RemoteFilesAtPull, addedBaseFiles);
        }

        if (session.PreserveDirectorySkeleton)
        {
            progress?.Report(new SyncProgress { Phase = "Synchronizing directory skeleton", Detail = $"Enumerating remote directories to depth {session.SkeletonDepth}...", Completed = 0, Total = 0 });
            var skeletonPaths = await Task.Run(() => GetDirectorySkeletonPaths(profile.RemoteRoot, session.SkeletonDepth, cancellationToken), cancellationToken);
            await CreateDirectorySkeletonAsync(profile.LocalRoot, skeletonPaths, session.SkeletonDepth, session.AppLogPath, progress, cancellationToken);
        }

        await _logService.AppendAsync(resumeLogPath, $"Pull backend: {backend.Name}", cancellationToken);
        if (backend is NativeSyncBackend)
        {
            var addedBaseFiles = await Task.Run(
                () => ScanAndPullNativeFoldersAsync(
                    profile,
                    backend,
                    added,
                    resumeLogPath,
                    progress,
                    cancellationToken),
                cancellationToken);
            session.Manifest.RemoteFilesAtPull = MergeFileStates(session.Manifest.RemoteFilesAtPull, addedBaseFiles);
        }
        else
        {
            await PullFoldersAsync(profile, backend, added, resumeLogPath, "Pulling added folder", progress, cancellationToken);
        }

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
        session.FailurePhase = "Preparing final sync";

        try
        {
            Directory.CreateDirectory(session.LogRoot);
            Directory.CreateDirectory(session.HistoryRoot);
            await _logService.WriteSessionHeaderAsync(session.PushLogPath, session, "End Work Session / Push", cancellationToken);

            session.SelectedPaths = NormalizeSelectedPaths(session.SelectedPaths);
            var baseFiles = DeduplicateFileStates(session.Manifest.RemoteFilesAtPull);

            var backend = _backendFactory.Create(profile);
            session.FailurePhase = "Comparing selected local and remote folders";
            progress?.Report(new SyncProgress { Phase = "Comparing selected local and remote folders", Detail = "", Completed = 0, Total = 0 });
            var streamedPush = await CompareAndPushSelectedFilesAsync(baseFiles, session, profile, backend, progress, cancellationToken);
            var plan = streamedPush.Plan;

            session.FailurePhase = "Categorizing unselected folders";
            progress?.Report(new SyncProgress { Phase = "Categorizing unselected folders", Detail = "", Completed = 0, Total = 0 });
            var unselectedPlan = BuildUnselectedPushPlan(session, progress, cancellationToken);
            await WriteCategorizedPushPlanAsync(session, unselectedPlan, cancellationToken);
            await _logService.WritePlanSummaryAsync(session.PushLogPath, plan, cancellationToken);

            if (plan.Conflicts.Count > 0)
            {
                var message = streamedPush.StartedOperationCount == 0
                    ? $"Found {plan.Conflicts.Count} conflict(s). No remote changes were made."
                    : $"Found {plan.Conflicts.Count} conflict(s). {streamedPush.StartedOperationCount} confirmed change(s) started before the conflict was found; no further changes were queued.";
                var firstConflict = plan.Conflicts[0];
                var failureDetails = $"{message} First conflict: {firstConflict.RelativePath} - {firstConflict.Reason}";
                await MarkSessionFailedAsync(session, new InvalidOperationException(failureDetails), "Resolving sync conflicts");

                return new WorkSessionEndResult
                {
                    Succeeded = false,
                    Message = message,
                    Plan = plan,
                    LogFolder = session.LogRoot
                };
            }

            if (backend.Name == "rclone")
            {
                session.FailurePhase = "Reconciling selected folder structure";
                progress?.Report(new SyncProgress { Kind = SyncProgressKind.Modification, Phase = "Reconciling selected folder structure", Detail = "rclone final pass", Completed = 0, Total = 0 });
                await MirrorPushSelectedFoldersAsync(backend, session, profile, progress, cancellationToken);
            }

            session.FailurePhase = "Updating unselected folders";
            await CopyUpdateUnselectedFoldersAsync(backend, session, profile, unselectedPlan.UpdateOnlyPaths, progress, cancellationToken);
            session.FailurePhase = "Cleaning old history";
            await _historyManager.CleanOldHistoryAsync(session.HistoryRoot, profile.HistoryRetentionDays, session.PushLogPath, cancellationToken);

            session.EndedAt = DateTimeOffset.Now;
            session.Status = SessionStatusService.Completed;
            session.FailurePhase = string.Empty;
            session.FailureDetails = string.Empty;
            session.FailedAt = null;
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
        catch (Exception exception)
        {
            await MarkSessionFailedAsync(session, exception, session.FailurePhase);
            throw;
        }
    }

    private async Task<StreamingPushResult> CompareAndPushSelectedFilesAsync(
        IReadOnlyCollection<FileState> baseFiles,
        WorkSession session,
        ProfileConfig profile,
        ISyncBackend backend,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var baseByPath = baseFiles.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var localByPath = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        var remoteByPath = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        var comparedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stateLock = new object();
        var planLock = new object();
        var taskLock = new object();
        var plan = new SyncPlan();
        var transferTasks = new List<Task>();
        using var transferGate = new SemaphoreSlim(MaxConcurrentFileChanges, MaxConcurrentFileChanges);

        var conflictDetected = 0;
        var startedOperations = 0;

        async Task CompareAndQueueAsync(string relativePath, FileState? localState, FileState? remoteState)
        {
            if (profile.ComparisonMode == ComparisonMode.Hybrid
                && localState is not null
                && remoteState is not null
                && !SyncPlanner.HasMatchingMetadata(localState, remoteState))
            {
                var localHashTask = _fileScanner.EnsureSha256Async(session.LocalRoot, localState, cancellationToken);
                var remoteHashTask = _fileScanner.EnsureSha256Async(session.RemoteRoot, remoteState, cancellationToken);
                await Task.WhenAll(localHashTask, remoteHashTask);
                localState = await localHashTask;
                remoteState = await remoteHashTask;
            }

            baseByPath.TryGetValue(relativePath, out var baseState);
            var decision = _planner.CreatePlanForPath(relativePath, baseState, localState, remoteState, profile.ComparisonMode);
            lock (planLock)
            {
                plan.Operations.AddRange(decision.Operations);
                plan.Conflicts.AddRange(decision.Conflicts);
            }

            if (decision.Conflicts.Count > 0)
            {
                Interlocked.Exchange(ref conflictDetected, 1);
                progress?.Report(new SyncProgress
                {
                    Kind = SyncProgressKind.Comparison,
                    Phase = "Conflict found; no further file changes will be queued",
                    Detail = relativePath
                });
                return;
            }

            var operation = decision.Operations.SingleOrDefault(item => item.Kind != SyncOperationKind.Skip);
            if (operation is null || Volatile.Read(ref conflictDetected) != 0)
            {
                return;
            }

            var transferTask = Task.Run(async () =>
            {
                await transferGate.WaitAsync(cancellationToken);
                try
                {
                    if (Volatile.Read(ref conflictDetected) != 0)
                    {
                        progress?.Report(new SyncProgress
                        {
                            Kind = SyncProgressKind.Modification,
                            Phase = "Skipping queued change after conflict",
                            Detail = operation.RelativePath
                        });
                        return;
                    }

                    Interlocked.Increment(ref startedOperations);
                    await _executor.ExecuteOperationAsync(operation, session, session.HistoryRoot, session.PushLogPath, backend, progress, cancellationToken);
                }
                finally
                {
                    transferGate.Release();
                }
            }, cancellationToken);

            lock (taskLock)
            {
                transferTasks.Add(transferTask);
            }
        }

        async Task ProcessScannedStateAsync(FileState state, bool isLocal)
        {
            FileState? localState = null;
            FileState? remoteState = null;
            var readyToCompare = false;
            lock (stateLock)
            {
                if (isLocal)
                {
                    localByPath[state.RelativePath] = state;
                    remoteByPath.TryGetValue(state.RelativePath, out remoteState);
                    localState = state;
                }
                else
                {
                    remoteByPath[state.RelativePath] = state;
                    localByPath.TryGetValue(state.RelativePath, out localState);
                    remoteState = state;
                }

                readyToCompare = localState is not null
                    && remoteState is not null
                    && comparedPaths.Add(state.RelativePath);
            }

            if (readyToCompare)
            {
                await CompareAndQueueAsync(state.RelativePath, localState, remoteState);
            }
        }

        async Task ScanSideAsync(string root, bool isLocal, string sourceLabel)
        {
            if (!isLocal && backend is IRemoteFileScanBackend)
            {
                var remoteStates = await ScanFileStatesAsync(profile, root, session.SelectedPaths, session.PushLogPath, progress, cancellationToken);
                foreach (var state in remoteStates)
                {
                    await ProcessScannedStateAsync(state, isLocal: false);
                }

                return;
            }

            await foreach (var state in _fileScanner.ScanFilesAsync(
                root,
                session.SelectedPaths,
                computeHashes: profile.ComparisonMode == ComparisonMode.Content,
                sourceLabel,
                progress,
                cancellationToken))
            {
                await ProcessScannedStateAsync(state, isLocal);
            }
        }

        var localScanTask = Task.Run(() => ScanSideAsync(session.LocalRoot, isLocal: true, sourceLabel: "local"), cancellationToken);
        var remoteScanTask = Task.Run(() => ScanSideAsync(session.RemoteRoot, isLocal: false, sourceLabel: "remote"), cancellationToken);
        await Task.WhenAll(localScanTask, remoteScanTask);

        var remainingPaths = new List<(string RelativePath, FileState? Local, FileState? Remote)>();
        lock (stateLock)
        {
            foreach (var relativePath in baseByPath.Keys
                .Concat(localByPath.Keys)
                .Concat(remoteByPath.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!comparedPaths.Add(relativePath))
                {
                    continue;
                }

                localByPath.TryGetValue(relativePath, out var localState);
                remoteByPath.TryGetValue(relativePath, out var remoteState);
                remainingPaths.Add((relativePath, localState, remoteState));
            }
        }

        foreach (var remaining in remainingPaths)
        {
            await CompareAndQueueAsync(remaining.RelativePath, remaining.Local, remaining.Remote);
        }

        Task[] scheduledTransfers;
        lock (taskLock)
        {
            scheduledTransfers = transferTasks.ToArray();
        }

        progress?.Report(new SyncProgress
        {
            Phase = "Applying queued file changes",
            Detail = scheduledTransfers.Length == 0
                ? "No selected-file changes require transfer"
                : $"{scheduledTransfers.Length} queued change(s)"
        });
        await Task.WhenAll(scheduledTransfers);
        return new StreamingPushResult(plan, Volatile.Read(ref startedOperations));
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

    private async Task MarkSessionFailedAsync(WorkSession session, Exception exception, string failurePhase)
    {
        session.Status = exception is OperationCanceledException
            ? SessionStatusService.Interrupted
            : SessionStatusService.Failed;
        session.FailedAt = DateTimeOffset.Now;
        session.FailurePhase = string.IsNullOrWhiteSpace(failurePhase) ? "Unknown phase" : failurePhase;
        session.FailureDetails = exception.Message;
        session.Events.Add(CreateSessionEvent("SessionFailed", session.SelectedPaths, session.AppLogPath));
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

    private async Task<IReadOnlyList<FileState>> ScanAndPullNativeFoldersAsync(
        ProfileConfig profile,
        ISyncBackend backend,
        IReadOnlyList<string> relativePaths,
        string logPath,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pendingTransfers = Channel.CreateBounded<FileState>(new BoundedChannelOptions(256)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var remoteFiles = new List<FileState>();
        var remotePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queuedCandidates = 0;

        var transferWorker = Task.Run(async () =>
        {
            try
            {
                await foreach (var remoteState in pendingTransfers.Reader.ReadAllAsync(cancellationToken))
                {
                    if (!await RequiresNativePullAsync(profile, remoteState, cancellationToken))
                    {
                        continue;
                    }

                    var source = PathSafety.CombineRootAndRelative(profile.RemoteRoot, remoteState.RelativePath);
                    var destination = PathSafety.CombineRootAndRelative(profile.LocalRoot, remoteState.RelativePath);
                    progress?.Report(new SyncProgress
                    {
                        Kind = SyncProgressKind.Modification,
                        Phase = "Copying/updating local file",
                        Detail = remoteState.RelativePath
                    });
                    await backend.CopyFileAsync(source, destination, new SyncBackendOptions
                    {
                        LogPath = logPath,
                        ComparisonMode = profile.ComparisonMode
                    }, cancellationToken);
                }
            }
            catch (Exception exception)
            {
                pendingTransfers.Writer.TryComplete(exception);
                throw;
            }
        }, CancellationToken.None);

        try
        {
            progress?.Report(new SyncProgress
            {
                Phase = "Scanning remote files and streaming native pull",
                Detail = $"{relativePaths.Count} folder(s)"
            });
            await foreach (var remoteState in _fileScanner.ScanFilesAsync(
                profile.RemoteRoot,
                relativePaths,
                computeHashes: profile.ComparisonMode == ComparisonMode.Content,
                comparisonSource: "remote",
                progress: progress,
                cancellationToken: cancellationToken))
            {
                remoteFiles.Add(remoteState);
                remotePaths.Add(remoteState.RelativePath);
                if (CouldRequireNativePull(profile, remoteState))
                {
                    await pendingTransfers.Writer.WriteAsync(remoteState, cancellationToken);
                    queuedCandidates++;
                }
            }

            pendingTransfers.Writer.TryComplete();
            await transferWorker;
        }
        catch
        {
            pendingTransfers.Writer.TryComplete();
            try
            {
                await transferWorker;
            }
            catch
            {
                // Preserve the scan or transfer failure that brought us here.
            }

            throw;
        }

        if (profile.PullMode == PullMode.Mirror)
        {
            await DeleteLocalFilesMissingFromRemoteAsync(profile, relativePaths, remotePaths, progress, cancellationToken);
        }

        ReconcileNativePullDirectories(profile, relativePaths, mirror: profile.PullMode == PullMode.Mirror, progress, cancellationToken);
        await _logService.AppendAsync(
            logPath,
            $"Native streaming pull complete: scanned={remoteFiles.Count}, queued candidates={queuedCandidates}.",
            cancellationToken);
        return remoteFiles.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool CouldRequireNativePull(ProfileConfig profile, FileState remoteState)
    {
        if (profile.ComparisonMode == ComparisonMode.Content)
        {
            return true;
        }

        var localPath = PathSafety.CombineRootAndRelative(profile.LocalRoot, remoteState.RelativePath);
        if (!File.Exists(localPath))
        {
            return true;
        }

        return !SyncPlanner.HasMatchingMetadata(remoteState, CreateFileState(profile.LocalRoot, remoteState.RelativePath));
    }

    private async Task<bool> RequiresNativePullAsync(
        ProfileConfig profile,
        FileState remoteState,
        CancellationToken cancellationToken)
    {
        var localPath = PathSafety.CombineRootAndRelative(profile.LocalRoot, remoteState.RelativePath);
        if (!File.Exists(localPath))
        {
            return true;
        }

        var localState = CreateFileState(profile.LocalRoot, remoteState.RelativePath);
        var metadataMatches = SyncPlanner.HasMatchingMetadata(remoteState, localState);
        if (profile.ComparisonMode == ComparisonMode.Fast)
        {
            return !metadataMatches;
        }

        if (profile.ComparisonMode == ComparisonMode.Hybrid && metadataMatches)
        {
            return false;
        }

        var remoteHashTask = _fileScanner.EnsureSha256Async(profile.RemoteRoot, remoteState, cancellationToken);
        var localHashTask = _fileScanner.EnsureSha256Async(profile.LocalRoot, localState, cancellationToken);
        await Task.WhenAll(remoteHashTask, localHashTask);
        return !string.Equals(
            (await remoteHashTask).Sha256,
            (await localHashTask).Sha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task DeleteLocalFilesMissingFromRemoteAsync(
        ProfileConfig profile,
        IReadOnlyList<string> relativePaths,
        IReadOnlySet<string> remotePaths,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new SyncProgress
        {
            Phase = "Reconciling local files removed from remote",
            Detail = string.Empty
        });
        await foreach (var localState in _fileScanner.ScanFilesAsync(
            profile.LocalRoot,
            relativePaths,
            computeHashes: false,
            comparisonSource: "local",
            progress: progress,
            cancellationToken: cancellationToken))
        {
            if (remotePaths.Contains(localState.RelativePath))
            {
                continue;
            }

            var localPath = PathSafety.CombineRootAndRelative(profile.LocalRoot, localState.RelativePath);
            File.Delete(localPath);
            progress?.Report(new SyncProgress
            {
                Kind = SyncProgressKind.Modification,
                Phase = "Deleting local file absent from remote",
                Detail = localState.RelativePath
            });
        }
    }

    private static void ReconcileNativePullDirectories(
        ProfileConfig profile,
        IReadOnlyList<string> relativePaths,
        bool mirror,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var relativePath in GetTopLevelPaths(relativePaths))
        {
            var remoteRoot = PathSafety.CombineRootAndRelative(profile.RemoteRoot, relativePath);
            var localRoot = PathSafety.CombineRootAndRelative(profile.LocalRoot, relativePath);
            if (!Directory.Exists(remoteRoot))
            {
                continue;
            }

            Directory.CreateDirectory(localRoot);
            var checkedDirectories = 0;
            foreach (var remoteDirectory in EnumerateDirectoriesSafe(remoteRoot, recurse: true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var childPath = PathSafety.GetRelativePath(remoteRoot, remoteDirectory);
                Directory.CreateDirectory(PathSafety.CombineRootAndRelative(localRoot, childPath));
                checkedDirectories++;
                if (checkedDirectories == 1 || checkedDirectories % 128 == 0)
                {
                    progress?.Report(new SyncProgress
                    {
                        Phase = $"Creating local directory structure ({checkedDirectories} checked)",
                        Detail = $"{relativePath}/{childPath}"
                    });
                }
            }

            if (!mirror)
            {
                continue;
            }

            foreach (var localDirectory in EnumerateDirectoriesSafe(localRoot, recurse: true).OrderByDescending(path => path.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var childPath = PathSafety.GetRelativePath(localRoot, localDirectory);
                var remoteDirectory = PathSafety.CombineRootAndRelative(remoteRoot, childPath);
                if (!Directory.Exists(remoteDirectory) && !Directory.EnumerateFileSystemEntries(localDirectory).Any())
                {
                    Directory.Delete(localDirectory);
                }
            }
        }
    }

    private static FileState CreateFileState(string root, string relativePath)
    {
        var path = PathSafety.CombineRootAndRelative(root, relativePath);
        var info = new FileInfo(path);
        return new FileState
        {
            RelativePath = relativePath,
            Size = info.Length,
            LastWriteTimeUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
        };
    }

    private async Task PullFoldersAsync(
        ProfileConfig profile,
        ISyncBackend backend,
        IReadOnlyList<string> relativePaths,
        string logPath,
        string phase,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
        => await SyncIndependentFoldersAsync(
            relativePaths,
            phase,
            logPath,
            progress,
            async (relativePath, taskLogPath, token) =>
            {
            var source = PathSafety.CombineRootAndRelative(profile.RemoteRoot, relativePath);
            var destination = PathSafety.CombineRootAndRelative(profile.LocalRoot, relativePath);
            var options = new SyncBackendOptions
            {
                LogPath = taskLogPath,
                Mirror = profile.PullMode == PullMode.Mirror,
                CreateEmptyDirectories = true,
                Progress = progress,
                ProgressPhase = "Copying/updating local file",
                ProgressPathPrefix = relativePath,
                ComparisonMode = profile.ComparisonMode
            };

            if (profile.PullMode == PullMode.Mirror)
            {
                    await backend.SyncAsync(source, destination, options, token);
            }
            else
            {
                    await backend.CopyAsync(source, destination, options, token);
            }
            },
            cancellationToken);

    private async Task MirrorPushSelectedFoldersAsync(
        ISyncBackend backend,
        WorkSession session,
        ProfileConfig profile,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
        => await SyncIndependentFoldersAsync(
            session.SelectedPaths,
            "Mirror-pushing selected folders",
            session.PushLogPath,
            progress,
            async (relativePath, taskLogPath, token) =>
            {
            var source = PathSafety.CombineRootAndRelative(session.LocalRoot, relativePath);
            var destination = PathSafety.CombineRootAndRelative(session.RemoteRoot, relativePath);
            await backend.SyncAsync(source, destination, new SyncBackendOptions
            {
                LogPath = taskLogPath,
                Mirror = true,
                CreateEmptyDirectories = true,
                Progress = progress,
                ProgressPhase = "Reconciling selected folder structure",
                ProgressPathPrefix = relativePath,
                ComparisonMode = profile.ComparisonMode
            }, token);
            },
            cancellationToken);

    private async Task CopyUpdateUnselectedFoldersAsync(
        ISyncBackend backend,
        WorkSession session,
        ProfileConfig profile,
        IReadOnlyList<string> updateOnlyPaths,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
        => await SyncIndependentFoldersAsync(
            updateOnlyPaths,
            "Update-pushing unselected folders",
            session.PushLogPath,
            progress,
            async (relativePath, taskLogPath, token) =>
            {
            var source = PathSafety.CombineRootAndRelative(session.LocalRoot, relativePath);
            if (!Directory.Exists(source))
            {
                return;
            }

            var destination = PathSafety.CombineRootAndRelative(session.RemoteRoot, relativePath);
            await backend.CopyAsync(source, destination, new SyncBackendOptions
            {
                LogPath = taskLogPath,
                Mirror = false,
                CreateEmptyDirectories = false,
                Progress = progress,
                ProgressPhase = "Copying/updating unselected local file",
                ProgressPathPrefix = relativePath,
                ComparisonMode = profile.ComparisonMode
            }, token);
            },
            cancellationToken);

    private async Task SyncIndependentFoldersAsync(
        IReadOnlyList<string> relativePaths,
        string phase,
        string sessionLogPath,
        IProgress<SyncProgress>? progress,
        Func<string, string, CancellationToken, Task> synchronizeAsync,
        CancellationToken cancellationToken)
    {
        var roots = GetTopLevelPaths(relativePaths);
        if (roots.Count == 0)
        {
            return;
        }

        if (roots.Count == 1)
        {
            var relativePath = roots[0];
            progress?.Report(new SyncProgress { Kind = SyncProgressKind.Modification, Phase = $"{phase} (1/1)", Detail = relativePath, Completed = 0, Total = 1 });
            await Task.Run(() => synchronizeAsync(relativePath, sessionLogPath, cancellationToken), cancellationToken);
            progress?.Report(new SyncProgress { Kind = SyncProgressKind.Modification, Phase = $"{phase} (1/1)", Detail = relativePath, Completed = 1, Total = 1 });
            return;
        }

        await _logService.AppendAsync(sessionLogPath, $"{phase}: queuing {roots.Count} independent folders on the single transfer worker.", cancellationToken);

        var started = 0;
        var completed = 0;
        await Parallel.ForEachAsync(roots, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrentFolderSyncs,
            CancellationToken = cancellationToken
        }, async (relativePath, token) =>
        {
            var taskNumber = Interlocked.Increment(ref started);
            progress?.Report(new SyncProgress
            {
                Kind = SyncProgressKind.Modification,
                Phase = $"{phase} ({Volatile.Read(ref completed)}/{roots.Count} complete, {taskNumber} started)",
                Detail = relativePath,
                Completed = Volatile.Read(ref completed),
                Total = roots.Count
            });

            await synchronizeAsync(relativePath, sessionLogPath, token);

            var completedCount = Interlocked.Increment(ref completed);
            progress?.Report(new SyncProgress
            {
                Kind = SyncProgressKind.Modification,
                Phase = $"{phase} ({completedCount}/{roots.Count})",
                Detail = relativePath,
                Completed = completedCount,
                Total = roots.Count
            });
        });
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

        var directoriesSeen = 0;
        foreach (var directory in EnumerateDirectoriesSafe(session.LocalRoot, recurse: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = PathSafety.GetRelativePath(session.LocalRoot, directory);
            directoriesSeen++;
            if (directoriesSeen == 1 || directoriesSeen % 128 == 0)
            {
                progress?.Report(new SyncProgress
                {
                    Phase = $"Categorizing unselected folders ({directoriesSeen} directories checked)",
                    Detail = relative
                });
            }
            if (IsInsideMetadataFolder(relative) || IsUnderAnySelectedPath(relative, selectedSet))
            {
                continue;
            }

            allUnselectedDirectories.Add(relative);
        }

        var filesEnumerated = 0;
        var filesSeen = 0;
        foreach (var file in EnumerateFilesSafe(session.LocalRoot, recurse: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = PathSafety.GetRelativePath(session.LocalRoot, file);
            filesEnumerated++;
            if (filesEnumerated == 1 || filesEnumerated % 128 == 0)
            {
                progress?.Report(new SyncProgress
                {
                    Phase = $"Categorizing unselected folders ({filesEnumerated} files checked)",
                    Detail = relative
                });
            }
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

    private sealed record StreamingPushResult(
        SyncPlan Plan,
        int StartedOperationCount);
}
