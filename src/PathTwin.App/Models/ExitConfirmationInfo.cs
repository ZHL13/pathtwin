namespace PathTwin.App.Models;

public sealed class ExitConfirmationInfo
{
    public string State { get; init; } = string.Empty;
    public string CurrentOperation { get; init; } = string.Empty;
    public string Warning { get; init; } = string.Empty;
}
