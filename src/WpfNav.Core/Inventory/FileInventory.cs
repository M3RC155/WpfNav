using Microsoft.Extensions.FileSystemGlobbing;

namespace WpfNav.Core.Inventory;

public enum FileKind { CSharp, Xaml, Project, Solution, Props, PackagesConfig }

public sealed record InventoryFile(string RelativePath, string FullPath, FileKind Kind, long Length, DateTime LastWriteUtc);

/// <summary>Lists the repo's source and project files, skipping build output, VCS folders and excluded globs.</summary>
public sealed class FileInventory
{
    static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    readonly RepoRoot _root;
    readonly Matcher? _excludes;

    public FileInventory(RepoRoot root, IReadOnlyList<string> excludeGlobs)
    {
        _root = root;
        if (excludeGlobs.Count == 0) return;
        _excludes = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var glob in excludeGlobs) _excludes.AddInclude(glob);
    }

    public static IReadOnlySet<string> SkippedDirectories { get; } = new HashSet<string>(
        ["bin", "obj", ".git", ".vs", "node_modules", "packages", "TestResults", "artifacts"], StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<InventoryFile> Enumerate(string? relativeFolder = null)
    {
        var start = relativeFolder is null ? _root.FullPath : _root.Resolve(relativeFolder);
        var files = new List<InventoryFile>();
        if (Directory.Exists(start)) Walk(new DirectoryInfo(start), files);
        return files;
    }

    public bool IsTracked(string relativePath) =>
        KindOf(relativePath) is not null
        && !relativePath.Split('/', '\\').SkipLast(1).Any(SkippedDirectories.Contains)
        && !IsExcluded(relativePath.Replace('\\', '/'));

    public InventoryFile? Stat(string relativePath)
    {
        if (!IsTracked(relativePath)) return null;
        var info = new FileInfo(_root.Resolve(relativePath));
        return info.Exists
            ? new InventoryFile(_root.ToRelative(info.FullName), info.FullName, KindOf(relativePath)!.Value, info.Length, info.LastWriteTimeUtc)
            : null;
    }

    public static FileKind? KindOf(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        // WPF markup compilation writes and deletes "{Project}_{random}_wpftmp.csproj" during every build.
        if (name.EndsWith("_wpftmp.csproj", StringComparison.OrdinalIgnoreCase)) return null;
        if (name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)) return FileKind.Props;
        if (name.Equals("packages.config", StringComparison.OrdinalIgnoreCase)) return FileKind.PackagesConfig;
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".cs" => FileKind.CSharp,
            ".xaml" => FileKind.Xaml,
            ".csproj" => FileKind.Project,
            ".sln" or ".slnx" => FileKind.Solution,
            _ => null,
        };
    }

    void Walk(DirectoryInfo dir, List<InventoryFile> files)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = dir.EnumerateFileSystemInfos("*", Options).ToList();
        }
        catch (Exception e) when (e is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return; // the folder vanished or became unreadable while we walked (branch switch, build output)
        }
        foreach (var entry in entries)
        {
            if (entry is DirectoryInfo sub)
            {
                if (!SkippedDirectories.Contains(sub.Name)) Walk(sub, files);
                continue;
            }
            var relative = _root.ToRelative(entry.FullName);
            if (KindOf(relative) is { } kind && !IsExcluded(relative))
                files.Add(new InventoryFile(relative, entry.FullName, kind, ((FileInfo)entry).Length, entry.LastWriteTimeUtc));
        }
    }

    bool IsExcluded(string relativePath) => _excludes is not null && _excludes.Match(relativePath).HasMatches;
}
