using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using WpfNav.Core.Inventory;

namespace WpfNav.Core.Projects;

/// <summary>Reads the .csproj paths listed in a .sln or .slnx file.</summary>
public static class SolutionReader
{
    public static async Task<SolutionInfo> ReadAsync(RepoRoot root, string solutionPath, CancellationToken ct)
    {
        var full = root.Resolve(solutionPath);
        var relative = root.ToRelative(full);
        var name = Path.GetFileName(full);
        try
        {
            if (SolutionSerializers.GetSerializerByMoniker(full) is not { } serializer) return new SolutionInfo(name, relative, []);
            var model = await serializer.OpenAsync(full, ct);
            var folder = Path.GetDirectoryName(full)!;
            var projects = new List<string>();
            foreach (var project in model.SolutionProjects)
            {
                var path = Path.GetFullPath(Path.Combine(folder, project.FilePath.Replace('\\', Path.DirectorySeparatorChar)));
                if (!path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;
                try { projects.Add(root.ToRelative(path)); }
                catch (WpfNavException) { }
            }
            return new SolutionInfo(name, relative, projects);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new SolutionInfo(name, relative, []);
        }
    }
}
