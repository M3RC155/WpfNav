namespace WpfNav.Core.Inventory;

/// <summary>The repository root. Resolves user-supplied paths and refuses any that escape it.</summary>
public sealed class RepoRoot
{
    public RepoRoot(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full)) throw new WpfNavException($"Repository root not found: {path}");
        FullPath = full;
    }

    public string FullPath { get; }

    /// <summary>Full path for a repo-relative or absolute path that lies inside the root.</summary>
    public string Resolve(string relativeOrAbsolute)
    {
        var normalized = relativeOrAbsolute.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(FullPath, normalized));
        if (!IsInside(full)) throw new WpfNavException($"Path is outside the repository root: {Output.Echo.Cut(relativeOrAbsolute)}");
        return full;
    }

    /// <summary>Repo-relative path with '/' separators.</summary>
    public string ToRelative(string fullPath)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath)); // "src/" and "src" are the same folder
        if (!IsInside(full)) throw new WpfNavException($"Path is outside the repository root: {Output.Echo.Cut(fullPath)}");
        return Path.GetRelativePath(FullPath, full).Replace(Path.DirectorySeparatorChar, '/');
    }

    bool IsInside(string full) =>
        full.Equals(FullPath, StringComparison.OrdinalIgnoreCase)
        || full.StartsWith(FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
