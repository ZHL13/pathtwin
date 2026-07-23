using System.Collections.ObjectModel;
using PathTwin.App.Configuration;
using PathTwin.App.Constants;
using PathTwin.App.Logging;
using PathTwin.App.Models;
using PathTwin.App.Platform;
using PathTwin.App.Services;
using PathTwin.App.Sync;

namespace PathTwin.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly ConfigService _configService;
    private readonly LogService _logService;
    private readonly ShellService _shellService;
    private readonly TaskSchedulerService _taskSchedulerService;
    private readonly DirectoryTreeService _directoryTreeService;
    private readonly WorkSessionService _sessionService;
    private AppConfig _config = new();
    private MainViewState _state = MainViewState.Setup;
    private MainViewState _stateBeforeSettings = MainViewState.Setup;
    private bool _isSettingsOpen;
    private bool _isBusy;
    private bool _isProfileLoading = true;
    private string _statusText = "Loading profile";
    private string _statusTextBeforeSettings = "Loading profile";
    private string _currentOperation = string.Empty;
    private string _progressDetail = string.Empty;
    private string _syncToolText = string.Empty;
    private int _progressCompleted;
    private int _progressTotal;
    private bool _isProgressIndeterminate = true;
    private string _remoteRoot = string.Empty;
    private string _localRoot = string.Empty;
    private string _historyRoot = string.Empty;
    private string _logRoot = string.Empty;
    private string _rclonePath = string.Empty;
    private bool _useRclone = true;
    private ComparisonMode _comparisonMode = ComparisonMode.Hybrid;
    private bool _preserveDirectorySkeleton = true;
    private int _skeletonDepth = 2;
    private int _historyRetentionDays = 7;
    private int _localCleanupDays = 7;
    private bool _enableAutomaticStartup;
    private string _startupWindowStart = "19:00";
    private string _startupWindowEnd = "21:00";
    private bool _startOnWake = true;
    private bool _startOnUnlock = true;
    private bool _startOnLogon = true;
    private bool _singleInstanceMode = true;
    private WorkSession? _activeSession;
    private PreviousSessionStatus? _previousSessionProblem;

    public MainWindowViewModel(
        string appName,
        ConfigService configService,
        LogService logService,
        ShellService shellService,
        TaskSchedulerService taskSchedulerService,
        DirectoryTreeService directoryTreeService,
        WorkSessionService sessionService)
    {
        AppName = appName;
        _configService = configService;
        _logService = logService;
        _shellService = shellService;
        _taskSchedulerService = taskSchedulerService;
        _directoryTreeService = directoryTreeService;
        _sessionService = sessionService;

        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => !IsBusy && !IsProfileLoading);
        CancelSettingsCommand = new RelayCommand(CancelSettings, () => !IsBusy && !IsProfileLoading);
        OpenSettingsCommand = new RelayCommand(OpenSettings, () => !IsBusy && !IsProfileLoading);
        LoadTreeCommand = new AsyncRelayCommand(LoadTreeAsync, () => !IsBusy && IsConfigured);
        DryRunPullCommand = new AsyncRelayCommand(DryRunPullAsync, () => !IsBusy && IsConfigured && SelectedPaths.Count > 0);
        StartSessionCommand = new AsyncRelayCommand(StartSessionAsync, () => !IsBusy && IsConfigured && SelectedPaths.Count > 0 && !HasPreviousSessionProblem);
        ForceStartSessionCommand = new AsyncRelayCommand(ForceStartSessionAsync, () => !IsBusy && IsConfigured && SelectedPaths.Count > 0 && HasPreviousSessionProblem);
        AddFolderCommand = new AsyncRelayCommand(BeginAddFolderAsync, () => !IsBusy && ActiveSession is not null);
        ResumeSyncCommand = new AsyncRelayCommand(ResumeSyncAsync, () => !IsBusy && ActiveSession is not null && GetNewSelectedPaths().Count > 0);
        CancelAddFolderCommand = new RelayCommand(CancelAddFolder, () => !IsBusy && ActiveSession is not null);
        EndSessionCommand = new AsyncRelayCommand(EndSessionAsync, () => !IsBusy && ActiveSession is not null);
        OpenLocalRootCommand = new RelayCommand(() => _shellService.OpenFolder(LocalRoot), () => !string.IsNullOrWhiteSpace(LocalRoot));
        OpenLogFolderCommand = new RelayCommand(() => _shellService.OpenFolder(LogRoot), () => !string.IsNullOrWhiteSpace(LogRoot));
        OpenPreviousSessionLogCommand = new RelayCommand(OpenPreviousSessionLog, () => HasPreviousSessionProblem);
        CreateTaskCommand = new AsyncRelayCommand(CreateOrUpdateTaskAsync, () => !IsBusy && IsConfigured && EnableAutomaticStartup);
        DeleteTaskCommand = new AsyncRelayCommand(DeleteTaskAsync, () => !IsBusy);
        TestTaskCommand = new AsyncRelayCommand(TestTaskAsync, () => !IsBusy);
    }

    public string AppName { get; }
    public ObservableCollection<DirectoryNodeViewModel> DirectoryTree { get; } = [];
    public ObservableCollection<string> SelectedPaths { get; } = [];
    public ObservableCollection<string> ActivityLines { get; } = [];
    public ObservableCollection<string> ComparisonLines { get; } = [];
    public ObservableCollection<string> ModificationLines { get; } = [];
    public ObservableCollection<ErrorReportItem> ErrorItems { get; } = [];
    public ObservableCollection<string> ActiveSessionPaths { get; } = [];

    public AsyncRelayCommand SaveSettingsCommand { get; }
    public RelayCommand CancelSettingsCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public AsyncRelayCommand LoadTreeCommand { get; }
    public AsyncRelayCommand DryRunPullCommand { get; }
    public AsyncRelayCommand StartSessionCommand { get; }
    public AsyncRelayCommand ForceStartSessionCommand { get; }
    public AsyncRelayCommand AddFolderCommand { get; }
    public AsyncRelayCommand ResumeSyncCommand { get; }
    public RelayCommand CancelAddFolderCommand { get; }
    public AsyncRelayCommand EndSessionCommand { get; }
    public RelayCommand OpenLocalRootCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand OpenPreviousSessionLogCommand { get; }
    public AsyncRelayCommand CreateTaskCommand { get; }
    public AsyncRelayCommand DeleteTaskCommand { get; }
    public AsyncRelayCommand TestTaskCommand { get; }

    private string _taskStatusText = string.Empty;
    public string TaskStatusText
    {
        get => _taskStatusText;
        private set => SetProperty(ref _taskStatusText, value);
    }

    public Func<ErrorReport, Task>? ShowErrorAsync { get; set; }
    public event Action? RequestExit;

    public ExitConfirmationInfo GetExitConfirmationInfo()
    {
        var state = State switch
        {
            MainViewState.SyncRunning => "Synchronization is running",
            MainViewState.SessionActive => "A work session is active",
            MainViewState.AddFolder => "Adding folders to an active session",
            MainViewState.Setup => "Profile setup",
            MainViewState.Error => "Error or conflict review",
            _ => "Ready"
        };
        var operation = State == MainViewState.SyncRunning && !string.IsNullOrWhiteSpace(CurrentOperation)
            ? CurrentOperation
            : StatusText;
        var warning = State switch
        {
            MainViewState.SyncRunning => "A file scan, copy, deletion, or history backup may be in progress. Force Exit stops PathTwin immediately. The session may be recorded as unfinished and will require recovery on the next launch.",
            MainViewState.SessionActive or MainViewState.AddFolder => "This work session has not been ended. Local changes may not have been pushed to the remote folder. Force Exit leaves the session unfinished so it can be reviewed on the next launch.",
            MainViewState.Setup => "Unsaved profile edits will be discarded.",
            MainViewState.Error => "The current error or conflict review will be closed. Resolve it before the next sync when possible.",
            _ => "PathTwin is not synchronizing. Force Exit closes the application now."
        };

        return new ExitConfirmationInfo
        {
            State = state,
            CurrentOperation = string.IsNullOrWhiteSpace(operation) ? "No active operation" : operation,
            Warning = warning
        };
    }

    public WorkSession? ActiveSession
    {
        get => _activeSession;
        private set
        {
            if (SetProperty(ref _activeSession, value))
            {
                OnPropertyChanged(nameof(ActiveSessionId));
                OnPropertyChanged(nameof(ActiveSessionStartedAt));
                RefreshActiveSessionPaths();
                RaiseCommands();
            }
        }
    }

    public string ActiveSessionId => ActiveSession?.SessionId ?? string.Empty;
    public string ActiveSessionStartedAt => ActiveSession?.StartedAt.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;

    public bool HasPreviousSessionProblem => _previousSessionProblem is not null
        && !SessionStatusService.IsPreviousSessionSafeToClean(_previousSessionProblem);

    public string PreviousSessionProblemTitle => _previousSessionProblem is null
        ? string.Empty
        : $"Previous session {_previousSessionProblem.SessionId} did not complete";

    public string PreviousSessionProblemDetails
    {
        get
        {
            if (_previousSessionProblem is null)
            {
                return string.Empty;
            }

            var lines = new List<string>
            {
                $"Status: {_previousSessionProblem.Status}",
                $"Started: {_previousSessionProblem.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}"
            };

            if (!string.IsNullOrWhiteSpace(_previousSessionProblem.FailurePhase))
            {
                lines.Add($"Problem occurred during: {_previousSessionProblem.FailurePhase}");
            }

            if (!string.IsNullOrWhiteSpace(_previousSessionProblem.FailureDetails))
            {
                lines.Add($"Details: {_previousSessionProblem.FailureDetails}");
            }
            else
            {
                lines.Add("No detailed error was saved. The application may have been closed before the session completed.");
            }

            if (!string.IsNullOrWhiteSpace(_previousSessionProblem.LastRecordedActivity))
            {
                lines.Add($"Last recorded activity: {_previousSessionProblem.LastRecordedActivity}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    private PreviousSessionStatus? PreviousSessionProblem
    {
        get => _previousSessionProblem;
        set
        {
            if (SetProperty(ref _previousSessionProblem, value))
            {
                OnPropertyChanged(nameof(HasPreviousSessionProblem));
                OnPropertyChanged(nameof(PreviousSessionProblemTitle));
                OnPropertyChanged(nameof(PreviousSessionProblemDetails));
                RaiseCommands();
            }
        }
    }

    private void RefreshActiveSessionPaths()
    {
        ActiveSessionPaths.Clear();
        if (ActiveSession is null)
        {
            return;
        }

        foreach (var path in ActiveSession.SelectedPaths)
        {
            ActiveSessionPaths.Add(path);
        }
    }

    public MainViewState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                NotifyViewVisibilityChanged();
            }
        }
    }

    public bool IsSetupVisible => State == MainViewState.Setup || _isSettingsOpen;
    public bool IsReadyVisible => State == MainViewState.Ready && !_isSettingsOpen;
    public bool IsSessionActiveVisible => State == MainViewState.SessionActive && !_isSettingsOpen;
    public bool IsAddFolderVisible => State == MainViewState.AddFolder && !_isSettingsOpen;
    public bool IsSyncRunningVisible => State == MainViewState.SyncRunning && !_isSettingsOpen;
    public bool IsErrorVisible => State == MainViewState.Error && !_isSettingsOpen;
    public bool IsSettingsButtonVisible =>
        (State == MainViewState.Ready || State == MainViewState.SessionActive || State == MainViewState.Error)
        && !_isSettingsOpen
        && !IsProfileLoading;
    public string SettingsPanelTitle => State == MainViewState.Setup ? "Profile Setup" : "Edit Profile";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommands();
            }
        }
    }

    public bool IsProfileLoading
    {
        get => _isProfileLoading;
        private set
        {
            if (SetProperty(ref _isProfileLoading, value))
            {
                NotifyViewVisibilityChanged();
                RaiseCommands();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentOperation
    {
        get => _currentOperation;
        private set => SetProperty(ref _currentOperation, value);
    }

    public string ProgressDetail
    {
        get => _progressDetail;
        private set => SetProperty(ref _progressDetail, value);
    }

    public string SyncToolText
    {
        get => _syncToolText;
        private set => SetProperty(ref _syncToolText, value);
    }

    public int ProgressCompleted
    {
        get => _progressCompleted;
        private set => SetProperty(ref _progressCompleted, value);
    }

    public int ProgressTotal
    {
        get => _progressTotal;
        private set => SetProperty(ref _progressTotal, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public string RemoteRoot
    {
        get => _remoteRoot;
        set => SetProperty(ref _remoteRoot, value);
    }

    public string LocalRoot
    {
        get => _localRoot;
        set
        {
            if (SetProperty(ref _localRoot, value) && string.IsNullOrWhiteSpace(LogRoot) && !string.IsNullOrWhiteSpace(value))
            {
                LogRoot = Path.Combine(value + AppConstants.LocalMetadataDirName, "logs");
            }
        }
    }

    public string HistoryRoot
    {
        get => _historyRoot;
        set => SetProperty(ref _historyRoot, value);
    }

    public string LogRoot
    {
        get => _logRoot;
        set => SetProperty(ref _logRoot, value);
    }

    public string RclonePath
    {
        get => _rclonePath;
        set => SetProperty(ref _rclonePath, value);
    }

    public bool UseRclone
    {
        get => _useRclone;
        set => SetProperty(ref _useRclone, value);
    }

    public ComparisonMode ComparisonMode
    {
        get => _comparisonMode;
        set => SetProperty(ref _comparisonMode, value);
    }

    public IReadOnlyList<ComparisonMode> ComparisonModes { get; } = Enum.GetValues<ComparisonMode>();

    public bool PreserveDirectorySkeleton
    {
        get => _preserveDirectorySkeleton;
        set => SetProperty(ref _preserveDirectorySkeleton, value);
    }

    public int SkeletonDepth
    {
        get => _skeletonDepth;
        set => SetProperty(ref _skeletonDepth, Math.Max(0, value));
    }

    public int HistoryRetentionDays
    {
        get => _historyRetentionDays;
        set => SetProperty(ref _historyRetentionDays, Math.Max(1, value));
    }

    public int LocalCleanupDays
    {
        get => _localCleanupDays;
        set => SetProperty(ref _localCleanupDays, Math.Max(1, value));
    }

    public bool EnableAutomaticStartup
    {
        get => _enableAutomaticStartup;
        set => SetProperty(ref _enableAutomaticStartup, value);
    }

    public string StartupWindowStart
    {
        get => _startupWindowStart;
        set => SetProperty(ref _startupWindowStart, value);
    }

    public string StartupWindowEnd
    {
        get => _startupWindowEnd;
        set => SetProperty(ref _startupWindowEnd, value);
    }

    public bool StartOnWake
    {
        get => _startOnWake;
        set => SetProperty(ref _startOnWake, value);
    }

    public bool StartOnUnlock
    {
        get => _startOnUnlock;
        set => SetProperty(ref _startOnUnlock, value);
    }

    public bool StartOnLogon
    {
        get => _startOnLogon;
        set => SetProperty(ref _startOnLogon, value);
    }

    public bool SingleInstanceMode
    {
        get => _singleInstanceMode;
        set => SetProperty(ref _singleInstanceMode, value);
    }

    private bool IsConfigured =>
        !string.IsNullOrWhiteSpace(RemoteRoot)
        && !string.IsNullOrWhiteSpace(LocalRoot)
        && !string.IsNullOrWhiteSpace(HistoryRoot)
        && !string.IsNullOrWhiteSpace(LogRoot);

    public async Task InitializeAsync()
    {
        try
        {
            _config = await _configService.LoadAsync();
            LoadProfileIntoProperties(_config.ActiveProfile);
            State = IsConfigured ? MainViewState.Ready : MainViewState.Setup;
            StatusText = IsConfigured ? "Ready" : "Profile setup required";
            IsProfileLoading = false;
            if (IsConfigured)
            {
                await RefreshPreviousSessionProblemAsync();
                await LoadTreeAsync();
            }
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception, "Initialization failed");
        }
        finally
        {
            IsProfileLoading = false;
        }
    }

    public void SetRemoteRootFromPicker(string path)
    {
        RemoteRoot = path;
    }

    public void SetLocalRootFromPicker(string path)
    {
        LocalRoot = path;
        LogRoot = Path.Combine(path + AppConstants.LocalMetadataDirName, "logs");
        HistoryRoot = Path.Combine(path + AppConstants.LocalMetadataDirName, "history");
    }
    public void SetHistoryRootFromPicker(string path) => HistoryRoot = path;
    public void SetLogRootFromPicker(string path) => LogRoot = path;

    private async Task SaveSettingsAsync()
    {
        try
        {
            IsBusy = true;
            CurrentOperation = "Saving settings";
            var originalProfile = CloneProfile(_config.ActiveProfile);
            var editedProfile = CloneProfile(_config.ActiveProfile);
            ApplySettingsPropertiesToProfile(editedProfile);

            var shouldEndActiveSession = ActiveSession is not null
                && HasSessionAffectingProfileChanges(originalProfile, editedProfile);

            if (shouldEndActiveSession)
            {
                _isSettingsOpen = false;
                State = MainViewState.SyncRunning;
                NotifyViewVisibilityChanged();
                CurrentOperation = "Ending current session before applying profile changes";

                var ended = await EndActiveSessionForProfileChangeAsync(originalProfile);
                if (!ended)
                {
                    return;
                }

                ActiveSession = null;
                DirectoryTree.Clear();
                SelectedPaths.Clear();
                editedProfile.LastSelectedPaths.Clear();
            }

            CopyProfileValues(editedProfile, _config.ActiveProfile);
            await _configService.SaveAsync(_config);
            _isSettingsOpen = false;
            var nextState = shouldEndActiveSession
                ? IsConfigured ? MainViewState.Ready : MainViewState.Setup
                : ResolveStateAfterSettings();
            State = nextState;
            NotifyViewVisibilityChanged();
            StatusText = shouldEndActiveSession
                ? "Settings saved. Previous session ended."
                : GetStatusTextAfterSettings(nextState, settingsSaved: true);
            AddActivity(shouldEndActiveSession
                ? "Settings saved after ending the active session."
                : "Settings saved.");
            if (nextState == MainViewState.Ready)
            {
                await RefreshPreviousSessionProblemAsync();
                await LoadTreeAsync();
            }
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception, "Failed to save settings");
        }
        finally
        {
            CurrentOperation = string.Empty;
            IsBusy = false;
        }
    }

    private void CancelSettings()
    {
        LoadProfileIntoProperties(_config.ActiveProfile);
        _isSettingsOpen = false;
        var nextState = ResolveStateAfterSettings();
        State = nextState;
        NotifyViewVisibilityChanged();
        StatusText = GetStatusTextAfterSettings(nextState, settingsSaved: false);
        RaiseCommands();
    }

    private void OpenSettings()
    {
        _stateBeforeSettings = State;
        _statusTextBeforeSettings = StatusText;
        LoadProfileIntoProperties(_config.ActiveProfile);
        _isSettingsOpen = true;
        NotifyViewVisibilityChanged();
        RaiseCommands();
        StatusText = "Settings";
    }

    private async Task<bool> EndActiveSessionForProfileChangeAsync(ProfileConfig originalProfile)
    {
        if (ActiveSession is null)
        {
            return true;
        }

        var progress = BeginSyncProgress();

        SyncToolText = FormatSyncToolText(_sessionService.GetSyncBackendName(originalProfile));
        var result = await _sessionService.EndAsync(ActiveSession, originalProfile, progress);
        if (!result.Succeeded)
        {
            State = MainViewState.Error;
            StatusText = result.Message;
            ErrorItems.Clear();
            foreach (var conflict in result.Plan.Conflicts)
            {
                ErrorItems.Add(new ErrorReportItem
                {
                    Path = conflict.RelativePath,
                    Details = conflict.Reason
                });
            }

            var report = new ErrorReport
            {
                Title = "Profile changes require ending the active session",
                LogFolder = result.LogFolder,
                Items = ErrorItems.ToList()
            };
            _shellService.OpenFolder(result.LogFolder);
            if (ShowErrorAsync is not null)
            {
                await ShowErrorAsync(report);
            }

            return false;
        }

        AddActivity(result.Message);
        return true;
    }

    private MainViewState ResolveStateAfterSettings()
    {
        if (!IsConfigured)
        {
            return MainViewState.Setup;
        }

        if (_stateBeforeSettings == MainViewState.SessionActive && ActiveSession is not null)
        {
            return MainViewState.SessionActive;
        }

        if (_stateBeforeSettings == MainViewState.Error)
        {
            return MainViewState.Error;
        }

        return MainViewState.Ready;
    }

    private string GetStatusTextAfterSettings(MainViewState nextState, bool settingsSaved)
    {
        if (nextState == MainViewState.SessionActive && ActiveSession is not null)
        {
            return settingsSaved
                ? $"Settings saved. Session {ActiveSession.SessionId} active"
                : $"Session {ActiveSession.SessionId} active";
        }

        if (!settingsSaved && !string.IsNullOrWhiteSpace(_statusTextBeforeSettings))
        {
            return _statusTextBeforeSettings;
        }

        return settingsSaved
            ? "Settings saved"
            : IsConfigured ? "Ready" : "Profile setup required";
    }

    private async Task LoadTreeAsync()
    {
        try
        {
            IsBusy = true;
            CurrentOperation = "Loading remote directory tree";
            await LoadDirectoryTreeForSelectionAsync(_config.ActiveProfile.LastSelectedPaths, []);

            StatusText = HasPreviousSessionProblem
                ? "Previous session needs recovery"
                : $"Loaded {DirectoryTree.Count} top-level folders";
            AddActivity($"Loaded remote tree from {RemoteRoot}.");
            RefreshSelectedPaths();
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception, "Failed to load remote directory tree");
        }
        finally
        {
            CurrentOperation = string.Empty;
            IsBusy = false;
        }
    }

    private async Task DryRunPullAsync()
    {
        try
        {
            IsBusy = true;
            CurrentOperation = "Running pull dry run";
            var profile = CaptureProfile();
            var logPath = await _sessionService.DryRunPullAsync(profile, SelectedPaths.ToArray());
            StatusText = "Dry run complete";
            AddActivity($"Dry run log: {logPath}");
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception, "Dry run failed");
        }
        finally
        {
            CurrentOperation = string.Empty;
            IsBusy = false;
        }
    }

    private Task StartSessionAsync() => StartSessionAsync(ignorePreviousSessionStatus: false);

    private Task ForceStartSessionAsync() => StartSessionAsync(ignorePreviousSessionStatus: true);

    private async Task StartSessionAsync(bool ignorePreviousSessionStatus)
    {
        try
        {
            IsBusy = true;
            State = MainViewState.SyncRunning;
            CurrentOperation = ignorePreviousSessionStatus
                ? "Ignoring previous session status and starting work session"
                : "Starting work session";
            var progress = BeginSyncProgress();

            var profile = CaptureProfile();
            SyncToolText = FormatSyncToolText(_sessionService.GetSyncBackendName(profile));
            var result = await _sessionService.StartAsync(
                profile,
                SelectedPaths.ToArray(),
                progress,
                ignorePreviousSessionStatus: ignorePreviousSessionStatus);
            ActiveSession = result.Session;
            PreviousSessionProblem = null;
            State = MainViewState.SessionActive;
            StatusText = result.Message;
            AddActivity(ignorePreviousSessionStatus
                ? $"Previous session status ignored. {result.Message}"
                : result.Message);

            // Remember selections for next time
            _config.ActiveProfile.LastSelectedPaths = SelectedPaths.ToList();
            _ = _configService.SaveAsync(_config);
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception, "Start Work Session failed");
        }
        finally
        {
            CurrentOperation = string.Empty;
            IsBusy = false;
        }
    }

    private async Task BeginAddFolderAsync()
    {
        if (ActiveSession is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            CurrentOperation = "Loading folders for Add Folder";
            State = MainViewState.AddFolder;
            await LoadDirectoryTreeForSelectionAsync(ActiveSession.SelectedPaths, ActiveSession.SelectedPaths);
            StatusText = "Add Folder";
            AddActivity("Add Folder mode opened.");
            RefreshSelectedPaths();
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception, "Failed to open Add Folder");
        }
        finally
        {
            CurrentOperation = string.Empty;
            IsBusy = false;
        }
    }

    private async Task ResumeSyncAsync()
    {
        if (ActiveSession is null)
        {
            return;
        }

        var newPaths = GetNewSelectedPaths();
        if (newPaths.Count == 0)
        {
            StatusText = "No new folders selected";
            return;
        }

        try
        {
            IsBusy = true;
            State = MainViewState.SyncRunning;
            CurrentOperation = "Resuming sync";
            var progress = BeginSyncProgress();

            var profile = CaptureProfile();
            SyncToolText = FormatSyncToolText(_sessionService.GetSyncBackendName(profile));
            var result = await _sessionService.ResumeWithAdditionalFoldersAsync(ActiveSession, profile, SelectedPaths.ToArray(), progress);
            RefreshActiveSessionPaths();
            State = MainViewState.SessionActive;
            StatusText = result.Message;
            AddActivity(result.Message);

            _config.ActiveProfile.LastSelectedPaths = ActiveSession.SelectedPaths.ToList();
            _ = _configService.SaveAsync(_config);
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception, "Resume Sync failed");
        }
        finally
        {
            CurrentOperation = string.Empty;
            IsBusy = false;
        }
    }

    private void CancelAddFolder()
    {
        DirectoryTree.Clear();
        SelectedPaths.Clear();
        State = MainViewState.SessionActive;
        StatusText = ActiveSession is null ? "Ready" : $"Session {ActiveSession.SessionId} active";
        AddActivity("Add Folder canceled.");
        RaiseCommands();
    }

    private async Task EndSessionAsync()
    {
        if (ActiveSession is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            State = MainViewState.SyncRunning;
            CurrentOperation = "Ending work session";
            var progress = BeginSyncProgress();

            var profile = CaptureProfile();
            SyncToolText = FormatSyncToolText(_sessionService.GetSyncBackendName(profile));
            var result = await _sessionService.EndAsync(ActiveSession, profile, progress);
            if (!result.Succeeded)
            {
                State = MainViewState.Error;
                StatusText = result.Message;
                ErrorItems.Clear();
                foreach (var conflict in result.Plan.Conflicts)
                {
                    ErrorItems.Add(new ErrorReportItem
                    {
                        Path = conflict.RelativePath,
                        Details = conflict.Reason
                    });
                }

                var report = new ErrorReport
                {
                    Title = "Sync conflicts",
                    LogFolder = result.LogFolder,
                    Items = ErrorItems.ToList()
                };
                _shellService.OpenFolder(result.LogFolder);
                if (ShowErrorAsync is not null)
                {
                    await ShowErrorAsync(report);
                }

                return;
            }

            StatusText = result.Message;
            AddActivity(result.Message);
            RequestExit?.Invoke();
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception, "End Work Session failed");
        }
        finally
        {
            CurrentOperation = string.Empty;
            IsBusy = false;
        }
    }

    private async Task RefreshPreviousSessionProblemAsync()
    {
        if (!IsConfigured)
        {
            PreviousSessionProblem = null;
            return;
        }

        var previousSession = await _sessionService.GetLatestPreviousSessionStatusAsync(LocalRoot);
        PreviousSessionProblem = SessionStatusService.IsPreviousSessionSafeToClean(previousSession)
            ? null
            : previousSession;

        if (HasPreviousSessionProblem)
        {
            StatusText = "Previous session needs recovery";
            AddActivity($"Previous session {_previousSessionProblem!.SessionId} is {_previousSessionProblem.Status}.");
        }
    }

    private void OpenPreviousSessionLog()
    {
        var logPath = _previousSessionProblem?.AppLogPath;
        var logFolder = string.IsNullOrWhiteSpace(logPath)
            ? LogRoot
            : Path.GetDirectoryName(logPath) ?? LogRoot;
        _shellService.OpenFolder(logFolder);
    }

    private async Task CreateOrUpdateTaskAsync()
    {
        try
        {
            IsBusy = true;
            CurrentOperation = "Managing scheduled task";
            var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AppConstants.ApplicationName + ".exe");
            var result = await _taskSchedulerService.CreateOrUpdateAsync(exePath, StartOnLogon, StartOnUnlock);
            TaskStatusText = result.Message;
            AddActivity($"Task Scheduler: {result.Message}");
            if (!result.Success)
            {
                await HandleExceptionAsync(new InvalidOperationException(result.Message), "Task Scheduler error");
            }
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception, "Task Scheduler operation failed");
        }
        finally
        {
            CurrentOperation = string.Empty;
            IsBusy = false;
        }
    }

    private async Task DeleteTaskAsync()
    {
        try
        {
            IsBusy = true;
            CurrentOperation = "Deleting scheduled task";
            var result = await _taskSchedulerService.DeleteAsync();
            TaskStatusText = result.Message;
            AddActivity($"Task Scheduler: {result.Message}");
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception, "Failed to delete task");
        }
        finally
        {
            CurrentOperation = string.Empty;
            IsBusy = false;
        }
    }

    private async Task TestTaskAsync()
    {
        try
        {
            IsBusy = true;
            CurrentOperation = "Testing scheduled task";
            var result = await _taskSchedulerService.TestRunAsync();
            TaskStatusText = result.Message;
            AddActivity($"Task Scheduler: {result.Message}");
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(exception, "Task test failed");
        }
        finally
        {
            CurrentOperation = string.Empty;
            IsBusy = false;
        }
    }

    private ProfileConfig CaptureProfile()
    {
        var profile = CloneProfile(_config.ActiveProfile);
        ApplySettingsPropertiesToProfile(profile);
        return profile;
    }

    private void ApplySettingsPropertiesToProfile(ProfileConfig profile)
    {
        profile.RemoteRoot = RemoteRoot.Trim();
        profile.LocalRoot = LocalRoot.Trim();
        profile.HistoryRoot = HistoryRoot.Trim();
        profile.LogRoot = LogRoot.Trim();
        profile.RclonePath = RclonePath.Trim();
        profile.UseRclone = UseRclone;
        profile.ComparisonMode = ComparisonMode;
        profile.PreserveDirectorySkeleton = PreserveDirectorySkeleton;
        profile.SkeletonDepth = SkeletonDepth;
        profile.HistoryRetentionDays = HistoryRetentionDays;
        profile.LocalCleanupDays = LocalCleanupDays;
        profile.EnableAutomaticStartup = EnableAutomaticStartup;
        profile.StartupWindowStart = StartupWindowStart.Trim();
        profile.StartupWindowEnd = StartupWindowEnd.Trim();
        profile.StartOnWake = StartOnWake;
        profile.StartOnUnlock = StartOnUnlock;
        profile.StartOnLogon = StartOnLogon;
        profile.SingleInstanceMode = SingleInstanceMode;
    }

    private static ProfileConfig CloneProfile(ProfileConfig profile)
    {
        var clone = new ProfileConfig();
        CopyProfileValues(profile, clone);
        return clone;
    }

    private static void CopyProfileValues(ProfileConfig source, ProfileConfig target)
    {
        target.Id = source.Id;
        target.Name = source.Name;
        target.RemoteRoot = source.RemoteRoot;
        target.LocalRoot = source.LocalRoot;
        target.HistoryRoot = source.HistoryRoot;
        target.LogRoot = source.LogRoot;
        target.RclonePath = source.RclonePath;
        target.UseRclone = source.UseRclone;
        target.ComparisonMode = source.ComparisonMode;
        target.PreserveDirectorySkeleton = source.PreserveDirectorySkeleton;
        target.SkeletonDepth = source.SkeletonDepth;
        target.PullMode = source.PullMode;
        target.PushMode = source.PushMode;
        target.HistoryRetentionDays = source.HistoryRetentionDays;
        target.LocalCleanupDays = source.LocalCleanupDays;
        target.EnableAutomaticStartup = source.EnableAutomaticStartup;
        target.StartupWindowStart = source.StartupWindowStart;
        target.StartupWindowEnd = source.StartupWindowEnd;
        target.StartOnWake = source.StartOnWake;
        target.StartOnUnlock = source.StartOnUnlock;
        target.StartOnLogon = source.StartOnLogon;
        target.SingleInstanceMode = source.SingleInstanceMode;
        target.LastSelectedPaths = [.. source.LastSelectedPaths];
    }

    private static bool HasSessionAffectingProfileChanges(ProfileConfig oldProfile, ProfileConfig newProfile)
        => !SameSetting(oldProfile.RemoteRoot, newProfile.RemoteRoot)
            || !SameSetting(oldProfile.LocalRoot, newProfile.LocalRoot)
            || !SameSetting(oldProfile.HistoryRoot, newProfile.HistoryRoot)
            || !SameSetting(oldProfile.LogRoot, newProfile.LogRoot)
            || !SameSetting(oldProfile.RclonePath, newProfile.RclonePath)
            || oldProfile.UseRclone != newProfile.UseRclone
            || oldProfile.ComparisonMode != newProfile.ComparisonMode
            || oldProfile.PreserveDirectorySkeleton != newProfile.PreserveDirectorySkeleton
            || oldProfile.SkeletonDepth != newProfile.SkeletonDepth
            || oldProfile.PullMode != newProfile.PullMode
            || oldProfile.PushMode != newProfile.PushMode
            || oldProfile.HistoryRetentionDays != newProfile.HistoryRetentionDays
            || oldProfile.LocalCleanupDays != newProfile.LocalCleanupDays;

    private static bool SameSetting(string left, string right)
        => string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string FormatSyncToolText(string backendName)
        => backendName switch
        {
            "rclone" => "Sync tool: rclone",
            "native-file-system" => "Sync tool: built-in file system",
            _ => $"Sync tool: {backendName}"
        };

    private IProgress<SyncProgress> BeginSyncProgress()
    {
        ProgressDetail = "";
        ProgressCompleted = 0;
        ProgressTotal = 0;
        IsProgressIndeterminate = true;
        ComparisonLines.Clear();
        ModificationLines.Clear();

        return new Progress<SyncProgress>(p =>
        {
            CurrentOperation = p.Phase;
            ProgressDetail = p.Detail;
            ProgressCompleted = p.Completed;
            ProgressTotal = p.Total;
            IsProgressIndeterminate = p.IsIndeterminate;

            switch (p.Kind)
            {
                case SyncProgressKind.Comparison:
                    AddSyncLine(ComparisonLines, p.Phase, p.Detail, includePhase: false);
                    break;
                case SyncProgressKind.Modification:
                    AddSyncLine(ModificationLines, p.Phase, p.Detail, includePhase: true);
                    break;
            }
        });
    }

    private static void AddSyncLine(ObservableCollection<string> target, string phase, string detail, bool includePhase)
    {
        if (string.IsNullOrWhiteSpace(detail) || detail == "Scan complete")
        {
            return;
        }

        const int maxLines = 300;
        if (target.Count >= maxLines)
        {
            target.RemoveAt(0);
        }

        target.Add(includePhase ? $"{phase}: {detail}" : detail);
    }

    private void LoadProfileIntoProperties(ProfileConfig profile)
    {
        RemoteRoot = profile.RemoteRoot;
        LocalRoot = profile.LocalRoot;
        HistoryRoot = profile.HistoryRoot;
        LogRoot = profile.LogRoot;
        RclonePath = profile.RclonePath;
        UseRclone = profile.UseRclone;
        ComparisonMode = profile.ComparisonMode;
        PreserveDirectorySkeleton = profile.PreserveDirectorySkeleton;
        SkeletonDepth = profile.SkeletonDepth;
        HistoryRetentionDays = profile.HistoryRetentionDays;
        LocalCleanupDays = profile.LocalCleanupDays;
        EnableAutomaticStartup = profile.EnableAutomaticStartup;
        StartupWindowStart = profile.StartupWindowStart;
        StartupWindowEnd = profile.StartupWindowEnd;
        StartOnWake = profile.StartOnWake;
        StartOnUnlock = profile.StartOnUnlock;
        StartOnLogon = profile.StartOnLogon;
        SingleInstanceMode = profile.SingleInstanceMode;
    }

    private async Task LoadDirectoryTreeForSelectionAsync(
        IReadOnlyCollection<string> restoredPaths,
        IReadOnlyCollection<string> lockedPaths)
    {
        DirectoryTree.Clear();
        SelectedPaths.Clear();

        var restored = new HashSet<string>(
            restoredPaths.Select(PathSafety.NormalizeRelativePath).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var locked = new HashSet<string>(
            lockedPaths.Select(PathSafety.NormalizeRelativePath).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string>? restoredPathSet = restored.Count > 0 ? restored : null;
        IReadOnlySet<string>? lockedPathSet = locked.Count > 0 ? locked : null;

        var nodes = await _directoryTreeService.LoadAsync(RemoteRoot);
        foreach (var node in nodes)
        {
            DirectoryTree.Add(new DirectoryNodeViewModel(
                node,
                parent: null,
                RefreshSelectedPaths,
                _directoryTreeService,
                RemoteRoot,
                restoredPathSet,
                lockedPathSet));
        }
    }

    private void RefreshSelectedPaths()
    {
        SelectedPaths.Clear();
        foreach (var root in DirectoryTree)
        {
            root.CollectSelectedTopLevel(SelectedPaths);
        }

        RaiseCommands();
    }

    private IReadOnlyList<string> GetNewSelectedPaths()
    {
        if (ActiveSession is null)
        {
            return [];
        }

        var existing = new HashSet<string>(ActiveSession.SelectedPaths, StringComparer.OrdinalIgnoreCase);
        return SelectedPaths
            .Select(PathSafety.NormalizeRelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && !IsUnderAnySelectedPath(path, existing))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private void AddActivity(string message)
    {
        ActivityLines.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (ActivityLines.Count > 100)
        {
            ActivityLines.RemoveAt(ActivityLines.Count - 1);
        }
    }

    private async Task HandleExceptionAsync(Exception exception, string title)
    {
        State = MainViewState.Error;
        StatusText = title;
        ErrorItems.Clear();
        ErrorItems.Add(new ErrorReportItem
        {
            Path = string.Empty,
            Details = exception.Message
        });

        var logFolder = string.IsNullOrWhiteSpace(LogRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName, "logs")
            : LogRoot;
        Directory.CreateDirectory(logFolder);
        var logPath = Path.Combine(logFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}_app.log");
        await _logService.WriteExceptionAsync(logPath, exception);
        _shellService.OpenFolder(logFolder);

        var report = new ErrorReport
        {
            Title = title,
            LogFolder = logFolder,
            Items = ErrorItems.ToList()
        };

        if (ShowErrorAsync is not null)
        {
            await ShowErrorAsync(report);
        }
    }

    private void RaiseCommands()
    {
        SaveSettingsCommand.RaiseCanExecuteChanged();
        CancelSettingsCommand.RaiseCanExecuteChanged();
        OpenSettingsCommand.RaiseCanExecuteChanged();
        LoadTreeCommand.RaiseCanExecuteChanged();
        DryRunPullCommand.RaiseCanExecuteChanged();
        StartSessionCommand.RaiseCanExecuteChanged();
        ForceStartSessionCommand.RaiseCanExecuteChanged();
        AddFolderCommand.RaiseCanExecuteChanged();
        ResumeSyncCommand.RaiseCanExecuteChanged();
        CancelAddFolderCommand.RaiseCanExecuteChanged();
        EndSessionCommand.RaiseCanExecuteChanged();
        OpenLocalRootCommand.RaiseCanExecuteChanged();
        OpenLogFolderCommand.RaiseCanExecuteChanged();
        OpenPreviousSessionLogCommand.RaiseCanExecuteChanged();
        CreateTaskCommand.RaiseCanExecuteChanged();
        DeleteTaskCommand.RaiseCanExecuteChanged();
        TestTaskCommand.RaiseCanExecuteChanged();
    }

    private void NotifyViewVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsSetupVisible));
        OnPropertyChanged(nameof(IsReadyVisible));
        OnPropertyChanged(nameof(IsSessionActiveVisible));
        OnPropertyChanged(nameof(IsAddFolderVisible));
        OnPropertyChanged(nameof(IsSyncRunningVisible));
        OnPropertyChanged(nameof(IsErrorVisible));
        OnPropertyChanged(nameof(IsSettingsButtonVisible));
        OnPropertyChanged(nameof(SettingsPanelTitle));
    }
}
