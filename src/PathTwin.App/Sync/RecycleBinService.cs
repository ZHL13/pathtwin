using Microsoft.VisualBasic.FileIO;

namespace PathTwin.App.Sync;

public sealed class RecycleBinService
{
    public Task RecycleAsync(
        StartWorkCleanupItem item,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (item.IsDirectory)
        {
            FileSystem.DeleteDirectory(
                item.SourcePath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin);
        }
        else
        {
            FileSystem.DeleteFile(
                item.SourcePath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin);
        }

        return Task.CompletedTask;
    }
}
