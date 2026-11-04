using WpfNav.Core.Inventory;

namespace WpfNav.Core.Projects;

/// <summary>Solutions and projects of the repo, with projects in dependency order (dependents first).</summary>
public sealed class ProjectModel
{
    readonly List<ProjectInfo> _byDirectoryLength;

    ProjectModel(IReadOnlyList<ProjectInfo> projects, IReadOnlyList<SolutionInfo> solutions)
    {
        Projects = projects;
        Solutions = solutions;
        _byDirectoryLength = projects.OrderByDescending(p => p.Directory.Length).ToList();
    }

    public static ProjectModel Empty { get; } = new([], []);

    public IReadOnlyList<SolutionInfo> Solutions { get; }

    public IReadOnlyList<ProjectInfo> Projects { get; }

    public static async Task<ProjectModel> LoadAsync(RepoRoot root, IReadOnlyList<InventoryFile> files, CancellationToken ct)
    {
        var projects = files.Where(f => f.Kind == FileKind.Project).Select(f => ProjectFileReader.Read(root, f.RelativePath)).ToList();
        var solutionPaths = files.Where(f => f.Kind == FileKind.Solution).Select(f => f.RelativePath).ToList();
        var chosen = solutionPaths
            .Where(s => s.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                || !solutionPaths.Contains(Path.ChangeExtension(s, ".slnx"), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);
        var solutions = await Task.WhenAll(chosen.Select(s => SolutionReader.ReadAsync(root, s, ct)));
        return new ProjectModel(DependencyOrder(projects), solutions);
    }

    public ProjectInfo? FindByName(string name) =>
        Projects.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The project whose folder is the longest prefix of the path.</summary>
    public ProjectInfo? ProjectForFile(string relativePath) =>
        _byDirectoryLength.FirstOrDefault(p =>
            p.Directory.Length == 0 || relativePath.StartsWith(p.Directory + "/", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ProjectInfo> ReferencedBy(ProjectInfo project) =>
        Projects.Where(p => p.ProjectReferences.Contains(project.Path, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>Kahn's algorithm over A→B (A references B), always taking the ready project with the smallest key.</summary>
    static List<ProjectInfo> DependencyOrder(List<ProjectInfo> projects)
    {
        var byPath = projects.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var incoming = projects.ToDictionary(p => p, _ => 0);
        foreach (var p in projects)
            foreach (var r in p.ProjectReferences)
                if (byPath.TryGetValue(r, out var target)) incoming[target]++;

        var key = Comparer<ProjectInfo>.Create((a, b) =>
        {
            var c = (a.Error is not null).CompareTo(b.Error is not null);
            if (c == 0) c = (a.Kind == ProjectKind.Test).CompareTo(b.Kind == ProjectKind.Test);
            if (c == 0) c = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
        });
        var ready = new SortedSet<ProjectInfo>(projects.Where(p => incoming[p] == 0), key);
        var ordered = new List<ProjectInfo>();
        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            ordered.Add(next);
            foreach (var r in next.ProjectReferences)
                if (byPath.TryGetValue(r, out var target) && --incoming[target] == 0) ready.Add(target);
        }
        ordered.AddRange(projects.Except(ordered).Order(key));
        return ordered;
    }
}
