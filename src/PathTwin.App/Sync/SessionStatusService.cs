using System.Text.Json;
using PathTwin.App.Constants;

namespace PathTwin.App.Sync;

public sealed class SessionStatusService
{
    public const string Active = "Active";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Interrupted = "Interrupted";

    public async Task<PreviousSessionStatus?> GetLatestPreviousSessionAsync(
        string localRoot,
        CancellationToken cancellationToken = default)
    {
        var sessionsDirectory = Path.Combine(localRoot + AppConstants.LocalMetadataDirName, AppConstants.SessionsDirectoryName);
        if (!Directory.Exists(sessionsDirectory))
        {
            return null;
        }

        var sessions = new List<PreviousSessionStatus>();
        foreach (var sessionPath in Directory.EnumerateFiles(sessionsDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            sessions.Add(await ReadSessionStatusAsync(sessionPath, cancellationToken));
        }

        return sessions
            .OrderByDescending(session => session.StartedAt)
            .ThenByDescending(session => session.SessionPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public static bool IsPreviousSessionSafeToClean(PreviousSessionStatus? previousSession)
        => previousSession is null
            || string.Equals(previousSession.Status, Completed, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeStatus(string? status, DateTimeOffset? endedAt = null)
    {
        if (string.Equals(status, Completed, StringComparison.OrdinalIgnoreCase))
        {
            return Completed;
        }

        if (string.Equals(status, Failed, StringComparison.OrdinalIgnoreCase))
        {
            return Failed;
        }

        if (string.Equals(status, Interrupted, StringComparison.OrdinalIgnoreCase))
        {
            return Interrupted;
        }

        if (string.Equals(status, Active, StringComparison.OrdinalIgnoreCase))
        {
            return Active;
        }

        return endedAt.HasValue ? Completed : Active;
    }

    private static async Task<PreviousSessionStatus> ReadSessionStatusAsync(
        string sessionPath,
        CancellationToken cancellationToken)
    {
        var fallbackTimestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(sessionPath), TimeSpan.Zero);
        var fallbackId = Path.GetFileNameWithoutExtension(sessionPath);

        try
        {
            var json = await File.ReadAllTextAsync(sessionPath, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var sessionId = TryGetString(root, "SessionId") ?? fallbackId;
            var startedAt = TryGetDateTimeOffset(root, "StartedAt") ?? fallbackTimestamp;
            var endedAt = TryGetDateTimeOffset(root, "EndedAt");
            var status = NormalizeStatus(TryGetString(root, "Status"), endedAt);

            return new PreviousSessionStatus(sessionId, status, startedAt, sessionPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PreviousSessionStatus(fallbackId, Interrupted, fallbackTimestamp, sessionPath);
        }
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var result))
        {
            return result;
        }

        return null;
    }
}

public sealed record PreviousSessionStatus(
    string SessionId,
    string Status,
    DateTimeOffset StartedAt,
    string SessionPath);
