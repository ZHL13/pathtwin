namespace PathTwin.App.Models;

public sealed class WorkSession
{
    public string SessionId { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public string FailurePhase { get; set; } = string.Empty;
    public string FailureDetails { get; set; } = string.Empty;
    public string RemoteRoot { get; set; } = string.Empty;
    public string LocalRoot { get; set; } = string.Empty;
    public string HistoryRoot { get; set; } = string.Empty;
    public string LogRoot { get; set; } = string.Empty;
    public int SkeletonDepth { get; set; } = 2;
    public List<string> InitialSelectedPaths { get; set; } = [];
    public List<string> AddedSelectedPaths { get; set; } = [];
    public List<string> SelectedPaths { get; set; } = [];
    public bool PreserveDirectorySkeleton { get; set; }
    public string PullLogPath { get; set; } = string.Empty;
    public List<string> AddedPullLogPaths { get; set; } = [];
    public string PushLogPath { get; set; } = string.Empty;
    public string AppLogPath { get; set; } = string.Empty;
    public List<SessionEvent> Events { get; set; } = [];
    public SessionManifest Manifest { get; set; } = new();
}
