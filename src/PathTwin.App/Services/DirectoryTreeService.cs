using PathTwin.App.Constants;
using PathTwin.App.Models;

namespace PathTwin.App.Services;

public sealed class DirectoryTreeService
{
    private const int MaxDirectoryNodes = 10000;

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
        var nodes = new List<DirectoryNode>();
        foreach (var directory in Directory.EnumerateDirectories(current, "*", new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        }).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
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
                Children = [], // lazy — loaded on expansion
                HasChildren = Directory.EnumerateDirectories(directory).Any()
            });
        }

        return nodes;
    }
}
