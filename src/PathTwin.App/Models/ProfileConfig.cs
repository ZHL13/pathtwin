using PathTwin.App.Constants;

namespace PathTwin.App.Models;

public sealed class ProfileConfig
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "Default";
    public string RemoteRoot { get; set; } = string.Empty;
    public string LocalRoot { get; set; } = string.Empty;
    public string HistoryRoot { get; set; } = string.Empty;
    public string LogRoot { get; set; } = string.Empty;
    public string RclonePath { get; set; } = AppConstants.DefaultRclonePath;
    public bool PreserveDirectorySkeleton { get; set; } = true;
    public int SkeletonDepth { get; set; } = 2;
    public PullMode PullMode { get; set; } = PullMode.Mirror;
    public PushMode PushMode { get; set; } = PushMode.SafeMirrorWithBackup;
    public int HistoryRetentionDays { get; set; } = 7;
    public int LocalCleanupDays { get; set; } = 7;
    public bool MoveCleanedContentToLocalTrash { get; set; } = true;
    public bool EnableAutomaticStartup { get; set; }
    public string StartupWindowStart { get; set; } = "19:00";
    public string StartupWindowEnd { get; set; } = "21:00";
    public bool StartOnWake { get; set; } = true;
    public bool StartOnUnlock { get; set; } = true;
    public bool StartOnLogon { get; set; } = true;
    public bool SingleInstanceMode { get; set; } = true;
    public List<string> LastSelectedPaths { get; set; } = [];

    public static ProfileConfig CreateDefault() => new();
}

public enum PullMode
{
    Mirror,
    Update
}

public enum PushMode
{
    SafeMirrorWithBackup
}
