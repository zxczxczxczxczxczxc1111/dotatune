namespace DotaTune.Core.Platform;

public static class PathCase
{
    /// <summary>
    /// Returns the path with the letter case the folders really have on disk. Steam keeps
    /// its folder in the registry in lower case ("d:/games/steam"), which is fine for
    /// Windows but looks wrong to a person. Paths that do not exist come back unchanged.
    /// </summary>
    public static string Resolve(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        try
        {
            string current = root.ToUpperInvariant();
            foreach (string part in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                string? match = Directory.EnumerateFileSystemEntries(current, part).FirstOrDefault();
                if (match is null)
                {
                    return fullPath;
                }

                current = match;
            }

            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return fullPath;
        }
    }
}
