using PathTwin.App.Constants;
using PathTwin.App.Services;

namespace PathTwin.App.Sync;

public sealed class LocalTrashService
{
    public string GetStartWorkTrashRoot(string localRoot, string sessionId)
        => Path.Combine(localRoot + AppConstants.LocalMetadataDirName, AppConstants.TrashDirectoryName, $"{sessionId}_StartWorkCleanup");

    public Task MoveToTrashAsync(
        StartWorkCleanupPlan plan,
        StartWorkCleanupItem item,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var destination = PathSafety.CombineRootAndRelative(plan.TrashRoot, item.RelativePath);
        PathSafety.EnsureInsideRoot(plan.TrashRoot, destination, "move cleaned content to local trash");

        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"Local trash destination already exists: {destination}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? plan.TrashRoot);

        if (item.IsDirectory)
        {
            Directory.Move(item.SourcePath, destination);
        }
        else
        {
            File.Move(item.SourcePath, destination);
        }

        return Task.CompletedTask;
    }
}
