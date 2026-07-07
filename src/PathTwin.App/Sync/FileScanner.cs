using System.Security.Cryptography;
using PathTwin.App.Constants;
using PathTwin.App.Models;
using PathTwin.App.Services;

namespace PathTwin.App.Sync;

public sealed class FileScanner
{
    public async Task<IReadOnlyList<FileState>> ScanAsync(
        string root,
        IReadOnlyCollection<string> selectedRelativePaths,
        bool computeHashes,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var states = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        var scanRoots = selectedRelativePaths.Count == 0
            ? new[] { string.Empty }
            : selectedRelativePaths;

        var scanned = 0;
        foreach (var selectedPath in scanRoots)
        {
            var normalizedSelection = PathSafety.NormalizeRelativePath(selectedPath);
            var absoluteSelection = PathSafety.CombineRootAndRelative(root, normalizedSelection);
            if (!Directory.Exists(absoluteSelection))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(absoluteSelection, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = PathSafety.GetRelativePath(root, file);
                if (states.ContainsKey(relative))
                {
                    continue;
                }

                if (IsInsideMetadataFolder(relative))
                {
                    continue;
                }

                scanned++;
                if (progress is not null)
                {
                    progress.Report(new SyncProgress { Phase = $"Synchronizing files ({scanned} scanned)", Detail = relative });
                }

                var info = new FileInfo(file);
                states[relative] = new FileState
                {
                    RelativePath = relative,
                    Size = info.Length,
                    LastWriteTimeUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    Sha256 = computeHashes ? await ComputeSha256Async(file, cancellationToken) : null
                };
            }
        }

        progress?.Report(new SyncProgress { Phase = $"Synchronizing files ({scanned} scanned)", Detail = "done" });
        return states.Values.OrderBy(state => state.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static bool IsInsideMetadataFolder(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            string.Equals(segment, AppConstants.LocalMetadataDirName, StringComparison.OrdinalIgnoreCase));
    }
}
