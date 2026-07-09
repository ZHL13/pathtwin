namespace PathTwin.App.Models;

public sealed class SessionManifest
{
    public string SessionId { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public DateTimeOffset CapturedAt { get; set; }
    public int SkeletonDepth { get; set; } = 2;
    public List<string> InitialSelectedPaths { get; set; } = [];
    public List<string> AddedSelectedPaths { get; set; } = [];
    public List<string> SelectedPaths { get; set; } = [];
    public List<SessionEvent> Events { get; set; } = [];
    public string InitialPullLogPath { get; set; } = string.Empty;
    public List<string> AddedPullLogPaths { get; set; } = [];
    public string FinalPushLogPath { get; set; } = string.Empty;
    public List<FileState> RemoteFilesAtPull { get; set; } = [];
}

public sealed class SessionEvent
{
    public string Type { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public List<string> Paths { get; set; } = [];
    public string LogPath { get; set; } = string.Empty;
}
