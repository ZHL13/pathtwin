namespace PathTwin.App.Constants;

public static class AppConstants
{
    public const string ApplicationName = "PathTwin";
    public const string ConfigDirectoryName = "PathTwin";
    public const string ConfigFileName = "config.json";
    public const string SessionsDirectoryName = "sessions";
    public const string TrashDirectoryName = "trash";
    public const string ToolsDirectoryName = "tools";
    public const string RcloneFileName = "rclone.exe";
    public const string SingleInstanceMutexName = "Global\\PathTwin.SingleInstance";
    public const string LocalMetadataDirName = ".pt";

    public static string DefaultRclonePath =>
        Path.Combine(AppContext.BaseDirectory, ToolsDirectoryName, RcloneFileName);
}
