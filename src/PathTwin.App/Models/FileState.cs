namespace PathTwin.App.Models;

public sealed class FileState
{
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset LastWriteTimeUtc { get; set; }
    public string? Sha256 { get; set; }
}
