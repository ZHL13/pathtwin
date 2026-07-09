using PathTwin.App.Constants;
using PathTwin.App.Logging;
using PathTwin.App.Services;

namespace PathTwin.App.Sync;

public sealed class StartWorkCleanupService
{
    private readonly LogService _logService;
    private readonly LocalTrashService _localTrashService;

    public StartWorkCleanupService(LogService logService, LocalTrashService localTrashService)
    {
        _logService = logService;
        _localTrashService = localTrashService;
    }

    public Task<StartWorkCleanupPlan> BuildCleanupPlanAsync(
        string localRoot,
        IReadOnlyCollection<string> selectedPaths,
        string sessionId,
        bool moveCleanedContentToLocalTrash,
        CancellationToken cancellationToken = default)
    {
        var localRootFullPath = Path.GetFullPath(localRoot);
        var selectedSet = selectedPaths
            .Select(PathSafety.NormalizeRelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = new List<StartWorkCleanupItem>();
        if (Directory.Exists(localRootFullPath))
        {
            AddCleanupItems(localRootFullPath, localRootFullPath, selectedSet, items, cancellationToken);
        }

        var plan = new StartWorkCleanupPlan(
            localRootFullPath,
            _localTrashService.GetStartWorkTrashRoot(localRoot, sessionId),
            moveCleanedContentToLocalTrash,
            selectedSet.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            items.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());

        return Task.FromResult(plan);
    }

    public async Task ExecuteCleanupPlanAsync(
        StartWorkCleanupPlan plan,
        string logPath,
        CancellationToken cancellationToken = default)
    {
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ValidatePlanItem(plan, item);

                if (!Directory.Exists(item.SourcePath) && !File.Exists(item.SourcePath))
                {
                    await _logService.AppendAsync(logPath, $"Start Work cleanup skipped missing item: {item.RelativePath}", cancellationToken);
                    continue;
                }

                if (plan.MoveCleanedContentToLocalTrash)
                {
                    await _localTrashService.MoveToTrashAsync(plan, item, cancellationToken);
                }
                else if (item.IsDirectory)
                {
                    Directory.Delete(item.SourcePath, recursive: true);
                }
                else
                {
                    File.Delete(item.SourcePath);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await _logService.AppendAsync(
                    logPath,
                    $"Start Work cleanup failed for '{item.RelativePath}': {exception.GetType().Name}: {exception.Message}",
                    cancellationToken);
                throw;
            }
        }
    }

    private static void AddCleanupItems(
        string localRoot,
        string directory,
        HashSet<string> selectedSet,
        ICollection<StartWorkCleanupItem> items,
        CancellationToken cancellationToken)
    {
        foreach (var entry in EnumerateFileSystemEntriesSafe(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            PathSafety.EnsureInsideRoot(localRoot, entry, "clean local content");
            var relative = PathSafety.GetRelativePath(localRoot, entry);
            if (IsInsideMetadataFolder(relative))
            {
                continue;
            }

            var attributes = GetAttributes(entry);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);

            if (IsUnderAnySelectedPath(relative, selectedSet))
            {
                continue;
            }

            if (isDirectory && IsAncestorOfAnySelectedPath(relative, selectedSet))
            {
                if (!isReparsePoint)
                {
                    AddCleanupItems(localRoot, entry, selectedSet, items, cancellationToken);
                }

                continue;
            }

            if (isDirectory && !isReparsePoint && ContainsMetadataDirectory(localRoot, entry))
            {
                AddCleanupItems(localRoot, entry, selectedSet, items, cancellationToken);
                continue;
            }

            items.Add(new StartWorkCleanupItem(relative, entry, isDirectory));
        }
    }

    private static void ValidatePlanItem(StartWorkCleanupPlan plan, StartWorkCleanupItem item)
    {
        PathSafety.EnsureInsideRoot(plan.LocalRoot, item.SourcePath, "clean local content");

        if (IsInsideMetadataFolder(item.RelativePath))
        {
            throw new InvalidOperationException($"Refusing to clean metadata path: {item.RelativePath}");
        }

        var selectedSet = plan.SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (IsUnderAnySelectedPath(item.RelativePath, selectedSet) || IsAncestorOfAnySelectedPath(item.RelativePath, selectedSet))
        {
            throw new InvalidOperationException($"Refusing to clean selected path: {item.RelativePath}");
        }
    }

    private static bool ContainsMetadataDirectory(string localRoot, string directory)
    {
        foreach (var childDirectory in EnumerateDirectoriesSafe(directory, recurse: true))
        {
            var relative = PathSafety.GetRelativePath(localRoot, childDirectory);
            if (IsInsideMetadataFolder(relative))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateFileSystemEntriesSafe(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = 0
            }).GetEnumerator();
        }
        catch (Exception exception) when (IsEnumerationException(exception))
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string item;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    item = enumerator.Current;
                }
                catch (Exception exception) when (IsEnumerationException(exception))
                {
                    yield break;
                }

                yield return item;
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string directory, bool recurse)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateDirectories(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = recurse,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).GetEnumerator();
        }
        catch (Exception exception) when (IsEnumerationException(exception))
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string item;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    item = enumerator.Current;
                }
                catch (Exception exception) when (IsEnumerationException(exception))
                {
                    yield break;
                }

                yield return item;
            }
        }
    }

    private static FileAttributes GetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            return FileAttributes.Normal;
        }
    }

    private static bool IsUnderAnySelectedPath(string relativePath, HashSet<string> selectedSet)
    {
        var normalized = PathSafety.NormalizeRelativePath(relativePath);
        foreach (var selected in selectedSet)
        {
            if (normalized.Equals(selected, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(selected + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAncestorOfAnySelectedPath(string relativePath, HashSet<string> selectedSet)
    {
        var normalized = PathSafety.NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return selectedSet.Any(selected => selected.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInsideMetadataFolder(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => string.Equals(segment, AppConstants.LocalMetadataDirName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEnumerationException(Exception exception)
        => exception is UnauthorizedAccessException
            or IOException
            or DirectoryNotFoundException
            or PathTooLongException;
}

public sealed record StartWorkCleanupPlan(
    string LocalRoot,
    string TrashRoot,
    bool MoveCleanedContentToLocalTrash,
    IReadOnlyList<string> SelectedPaths,
    IReadOnlyList<StartWorkCleanupItem> Items);

public sealed record StartWorkCleanupItem(
    string RelativePath,
    string SourcePath,
    bool IsDirectory);
