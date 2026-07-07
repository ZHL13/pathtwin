namespace PathTwin.App.Models;

public sealed class SessionManifest
{
    public string SessionId { get; set; } = string.Empty;
    public DateTimeOffset CapturedAt { get; set; }
    public List<FileState> RemoteFilesAtPull { get; set; } = [];
}
