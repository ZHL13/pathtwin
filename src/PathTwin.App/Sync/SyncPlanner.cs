using PathTwin.App.Models;

namespace PathTwin.App.Sync;

public sealed class SyncPlanner
{
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    public SyncPlan CreatePlan(
        IReadOnlyCollection<FileState> baseFiles,
        IReadOnlyCollection<FileState> localFiles,
        IReadOnlyCollection<FileState> remoteFiles)
    {
        var plan = new SyncPlan();
        var baseByPath = baseFiles.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var localByPath = localFiles.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var remoteByPath = remoteFiles.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);

        var allPaths = baseByPath.Keys
            .Concat(localByPath.Keys)
            .Concat(remoteByPath.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in allPaths)
        {
            baseByPath.TryGetValue(relativePath, out var baseState);
            localByPath.TryGetValue(relativePath, out var localState);
            remoteByPath.TryGetValue(relativePath, out var remoteState);

            AddDecision(plan, relativePath, baseState, localState, remoteState);
        }

        return plan;
    }

    private static void AddDecision(
        SyncPlan plan,
        string relativePath,
        FileState? baseState,
        FileState? localState,
        FileState? remoteState)
    {
        if (baseState is null)
        {
            AddDecisionForNewFile(plan, relativePath, localState, remoteState);
            return;
        }

        var localChanged = !AreSame(baseState, localState);
        var remoteChanged = !AreSame(baseState, remoteState);

        if (localState is null && remoteState is null)
        {
            plan.Operations.Add(Skip(relativePath, "File already absent locally and remotely."));
            return;
        }

        if (localState is null)
        {
            if (!remoteChanged)
            {
                plan.Operations.Add(new SyncOperation
                {
                    Kind = SyncOperationKind.DeleteRemote,
                    RelativePath = relativePath,
                    Reason = "Local file was deleted inside a selected folder.",
                    RequiresRemoteBackup = true
                });
            }
            else
            {
                plan.Conflicts.Add(CreateConflict(relativePath, baseState, localState, remoteState, "Local deleted while remote changed."));
            }

            return;
        }

        if (remoteState is null)
        {
            plan.Conflicts.Add(CreateConflict(relativePath, baseState, localState, remoteState, "Remote file disappeared after pull."));
            return;
        }

        if (!localChanged && !remoteChanged)
        {
            plan.Operations.Add(Skip(relativePath, "Unchanged."));
            return;
        }

        if (localChanged && !remoteChanged)
        {
            plan.Operations.Add(new SyncOperation
            {
                Kind = SyncOperationKind.OverwriteRemote,
                RelativePath = relativePath,
                Reason = "Only local changed.",
                RequiresRemoteBackup = true
            });
            return;
        }

        if (!localChanged && remoteChanged)
        {
            plan.Conflicts.Add(CreateConflict(relativePath, baseState, localState, remoteState, "Remote changed while local did not change."));
            return;
        }

        if (AreSame(localState, remoteState))
        {
            plan.Operations.Add(Skip(relativePath, "Local and remote independently reached the same content."));
            return;
        }

        plan.Conflicts.Add(CreateConflict(relativePath, baseState, localState, remoteState, "Both local and remote changed."));
    }

    private static void AddDecisionForNewFile(
        SyncPlan plan,
        string relativePath,
        FileState? localState,
        FileState? remoteState)
    {
        if (localState is not null && remoteState is null)
        {
            plan.Operations.Add(new SyncOperation
            {
                Kind = SyncOperationKind.UploadNew,
                RelativePath = relativePath,
                Reason = "New local file.",
                RequiresRemoteBackup = false
            });
            return;
        }

        if (localState is null && remoteState is not null)
        {
            plan.Conflicts.Add(CreateConflict(relativePath, null, localState, remoteState, "New remote file appeared after pull."));
            return;
        }

        if (localState is not null && remoteState is not null)
        {
            if (AreSame(localState, remoteState))
            {
                plan.Operations.Add(Skip(relativePath, "New file already matches remote."));
            }
            else
            {
                plan.Conflicts.Add(CreateConflict(relativePath, null, localState, remoteState, "New file exists both locally and remotely with different content."));
            }
        }
    }

    private static SyncOperation Skip(string relativePath, string reason) => new()
    {
        Kind = SyncOperationKind.Skip,
        RelativePath = relativePath,
        Reason = reason
    };

    private static bool AreSame(FileState? left, FileState? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.Size != right.Size)
        {
            return false;
        }

        if ((left.LastWriteTimeUtc - right.LastWriteTimeUtc).Duration() <= TimestampTolerance)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(left.Sha256) && !string.IsNullOrWhiteSpace(right.Sha256))
        {
            return string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static SyncConflict CreateConflict(
        string relativePath,
        FileState? baseState,
        FileState? localState,
        FileState? remoteState,
        string reason)
        => new()
        {
            RelativePath = relativePath,
            LocalSize = localState?.Size,
            LocalModifiedUtc = localState?.LastWriteTimeUtc,
            RemoteSize = remoteState?.Size,
            RemoteModifiedUtc = remoteState?.LastWriteTimeUtc,
            BaseModifiedUtc = baseState?.LastWriteTimeUtc,
            Reason = reason
        };
}
