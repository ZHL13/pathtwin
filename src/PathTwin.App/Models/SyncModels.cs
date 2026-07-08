namespace PathTwin.App.Models;

public sealed class SyncPlan
{
    public List<SyncOperation> Operations { get; set; } = [];
    public List<SyncConflict> Conflicts { get; set; } = [];

    public int UploadCount => Operations.Count(o => o.Kind is SyncOperationKind.UploadNew or SyncOperationKind.OverwriteRemote);
    public int DeleteCount => Operations.Count(o => o.Kind == SyncOperationKind.DeleteRemote);
    public int SkipCount => Operations.Count(o => o.Kind == SyncOperationKind.Skip);
}

public sealed class SyncOperation
{
    public SyncOperationKind Kind { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public bool RequiresRemoteBackup { get; set; }
}

public enum SyncOperationKind
{
    UploadNew,
    OverwriteRemote,
    DeleteRemote,
    Skip
}

public sealed class SyncConflict
{
    public string RelativePath { get; set; } = string.Empty;
    public long? LocalSize { get; set; }
    public DateTimeOffset? LocalModifiedUtc { get; set; }
    public long? RemoteSize { get; set; }
    public DateTimeOffset? RemoteModifiedUtc { get; set; }
    public DateTimeOffset? BaseModifiedUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class WorkSessionStartResult
{
    public required WorkSession Session { get; init; }
    public required string Message { get; init; }
}

public sealed class WorkSessionResumeResult
{
    public required WorkSession Session { get; init; }
    public required IReadOnlyList<string> AddedPaths { get; init; }
    public required string Message { get; init; }
}

public sealed class WorkSessionEndResult
{
    public bool Succeeded { get; init; }
    public required string Message { get; init; }
    public SyncPlan Plan { get; init; } = new();
    public string LogFolder { get; init; } = string.Empty;
}

public sealed class ErrorReport
{
    public string Title { get; init; } = "Error";
    public List<ErrorReportItem> Items { get; init; } = [];
    public string LogFolder { get; init; } = string.Empty;
}

public sealed class ErrorReportItem
{
    public string Path { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
}

public sealed class SyncProgress
{
    public string Phase { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public int Completed { get; init; }
    public int Total { get; init; }
    public bool IsIndeterminate => Total == 0;
}
