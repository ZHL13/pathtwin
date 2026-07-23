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
            var appLogPath = TryGetString(root, "AppLogPath");
            var failureDetails = TryGetString(root, "FailureDetails");
            if (string.IsNullOrWhiteSpace(failureDetails))
            {
                failureDetails = await TryReadLatestErrorAsync(appLogPath, cancellationToken);
            }

            return new PreviousSessionStatus(
                sessionId,
                status,
                startedAt,
                endedAt,
                TryGetString(root, "FailurePhase"),
                failureDetails,
                TryGetLastEvent(root),
                appLogPath,
                sessionPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PreviousSessionStatus(
                fallbackId,
                Interrupted,
                fallbackTimestamp,
                null,
                "Reading session metadata",
                $"The session record could not be read: {exception.Message}",
                null,
                null,
                sessionPath);
        }
    }

    private static string? TryGetLastEvent(JsonElement root)
    {
        if (!root.TryGetProperty("Events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in events.EnumerateArray().Reverse())
        {
            var type = TryGetString(item, "Type");
            if (string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            var timestamp = TryGetDateTimeOffset(item, "Timestamp");
            return timestamp.HasValue
                ? $"{type} at {timestamp.Value.LocalDateTime:yyyy-MM-dd HH:mm:ss}"
                : type;
        }

        return null;
    }

    private static async Task<string?> TryReadLatestErrorAsync(string? appLogPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appLogPath) || !File.Exists(appLogPath))
        {
            return null;
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(appLogPath, cancellationToken);
            var errorLine = lines.LastOrDefault(line => line.Contains("ERROR:", StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(errorLine))
            {
                return null;
            }

            var errorIndex = errorLine.IndexOf("ERROR:", StringComparison.Ordinal);
            return errorLine[(errorIndex + "ERROR:".Length)..].Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
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
    DateTimeOffset? EndedAt,
    string? FailurePhase,
    string? FailureDetails,
    string? LastRecordedActivity,
    string? AppLogPath,
    string SessionPath);
