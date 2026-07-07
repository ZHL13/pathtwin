using PathTwin.App.Constants;
using PathTwin.App.Models;

namespace PathTwin.App.Services;

public sealed class DirectoryTreeService
{
    private const int MaxDirectoriesPerLevel = 300;

    /// <summary>Loads only the top-level directories (no recursion).</summary>
    public Task<IReadOnlyList<DirectoryNode>> LoadAsync(
        string remoteRoot,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DirectoryNode>>(() =>
        {
            if (string.IsNullOrWhiteSpace(remoteRoot) || !Directory.Exists(remoteRoot))
            {
                throw new DirectoryNotFoundException($"Remote root does not exist: {remoteRoot}");
            }

            return LoadOneLevel(remoteRoot, remoteRoot, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Loads the immediate children of a specific directory.</summary>
    public Task<IReadOnlyList<DirectoryNode>> LoadChildrenAsync(
        string remoteRoot,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DirectoryNode>>(() =>
        {
            var absolutePath = PathSafety.CombineRootAndRelative(remoteRoot, relativePath);
            if (!Directory.Exists(absolutePath))
            {
                return Array.Empty<DirectoryNode>();
            }

            return LoadOneLevel(remoteRoot, absolutePath, cancellationToken);
        }, cancellationToken);
    }

    private static List<DirectoryNode> LoadOneLevel(
        string root,
        string current,
        CancellationToken cancellationToken)
    {
        var directories = new List<string>(MaxDirectoriesPerLevel + 1);
        foreach (var directory in EnumerateDirectoriesSafe(current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            directories.Add(directory);
            if (directories.Count > MaxDirectoriesPerLevel)
            {
                break;
            }
        }

        var hasMore = directories.Count > MaxDirectoriesPerLevel;
        if (hasMore)
        {
            directories.RemoveAt(directories.Count - 1);
        }

        var nodes = new List<DirectoryNode>();
        foreach (var directory in directories.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dirName = Path.GetFileName(directory);
            if (string.Equals(dirName, AppConstants.LocalMetadataDirName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = PathSafety.GetRelativePath(root, directory);
            nodes.Add(new DirectoryNode
            {
                Name = dirName,
                RelativePath = relative,
                Children = [],
                // Avoid probing every child directory on large/slow SMB shares. Empty folders
                // simply collapse to no children after the user expands them.
                HasChildren = true
            });
        }

        if (hasMore)
        {
            nodes.Add(new DirectoryNode
            {
                Name = $"More than {MaxDirectoriesPerLevel} folders here. Choose a narrower root or subfolder.",
                RelativePath = string.Empty,
                IsSelectable = false,
                IsLimitNotice = true
            });
        }

        return nodes;
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string current)
    {
        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateDirectories(current, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).GetEnumerator();
        }
        catch (Exception ex) when (IsEnumerationException(ex))
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string directory;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    directory = enumerator.Current;
                }
                catch (Exception ex) when (IsEnumerationException(ex))
                {
                    yield break;
                }

                yield return directory;
            }
        }
    }

    private static bool IsEnumerationException(Exception exception)
        => exception is UnauthorizedAccessException
            or IOException
            or DirectoryNotFoundException
            or PathTooLongException;
}
