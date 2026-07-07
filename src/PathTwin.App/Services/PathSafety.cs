namespace PathTwin.App.Services;

public static class PathSafety
{
    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(normalized) || normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Invalid relative path: {relativePath}");
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment != ".")
            .ToArray();

        if (segments.Any(segment => segment == ".." || segment.IndexOfAny(invalidChars) >= 0))
        {
            throw new InvalidOperationException($"Invalid relative path: {relativePath}");
        }

        return string.Join('/', segments);
    }

    public static string CombineRootAndRelative(string root, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrEmpty(normalized))
        {
            return root;
        }

        var result = root;
        foreach (var segment in normalized.Split('/'))
        {
            result = Path.Combine(result, segment);
        }

        return result;
    }

    public static string ToDisplayPath(string relativePath) => NormalizeRelativePath(relativePath).Replace('\\', '/');

    public static string GetRelativePath(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        return NormalizeRelativePath(relative);
    }

    public static bool IsInsideRoot(string root, string candidatePath)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidatePath);

        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                fullCandidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureInsideRoot(string root, string candidatePath, string action)
    {
        if (!IsInsideRoot(root, candidatePath))
        {
            throw new InvalidOperationException($"Refusing to {action} outside configured root. Root='{root}', Path='{candidatePath}'.");
        }
    }
}
