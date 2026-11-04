using System.Globalization;
using System.Text;
using WpfNav.Core.Index;
using WpfNav.Core.Inventory;
using WpfNav.Core.Model;
using WpfNav.Core.Output;
using WpfNav.Core.Projects;

namespace WpfNav.Core.Navigation;

/// <summary>Renders the overview tool's text for the whole repo, one project, or one folder.</summary>
public static class OverviewBuilder
{
    // Section quotas are shares of the selected budget; each is also capped by what is left of it.
    const double SolutionsShare = 0.08, ProjectsShare = 0.5, SectionShare = 0.12;
    const int MaxPackages = 15, MaxEntryPoints = 10, MaxTopFolders = 10, ExpandedFolders = 3, MaxSubfolders = 5, MaxLargestFiles = 5;
    const string RepoHint = "pass scope=<project or folder> for detail", ScopedHint = "narrow with scope=<folder> or raise max_tokens";

    public static IReadOnlyList<string> RoleSuffixes { get; } =
        ["ViewModel", "View", "Window", "Page", "Control", "Dialog", "Service", "Repository", "Converter", "Behavior", "Command", "Model"];

    static readonly IReadOnlyList<string> SuffixesLongestFirst = RoleSuffixes.OrderByDescending(s => s.Length).ToList();

    public static Task<string> BuildAsync(Workspace ws, string? scope, CancellationToken ct) => BuildAsync(ws, scope, maxTokens: null, ct);

    /// <param name="maxTokens">Estimated-token budget for the whole result; null selects <see cref="Limits.Overview"/>.</param>
    public static async Task<string> BuildAsync(Workspace ws, string? scope, int? maxTokens, CancellationToken ct)
    {
        var budget = Budget.Resolve(maxTokens, Limits.Overview);
        var projects = await ws.GetProjectsAsync(ct);
        var index = await ws.GetIndexAsync(ct);
        var w = new OutputWriter(budget, ws.Root.FullPath);
        if (scope is null)
        {
            Repo(w, projects, index);
        }
        else if (projects.FindByName(scope) is { } project)
        {
            Project(w, projects, index, project);
        }
        else if (FolderOf(ws, scope) is { } folder)
        {
            if (folder.Length == 0) Repo(w, projects, index);
            else Folder(w, projects, index, folder);
        }
        else
        {
            var closest = SymbolMatcher.ClosestNames(projects.Projects.Select(p => p.Name), scope, 5);
            throw new WpfNavException($"Unknown scope '{Echo.Cut(scope)}'. Pass a project name or a folder."
                + (closest.Count == 0 ? "" : $" Closest projects: {string.Join(", ", closest)}"));
        }
        return w.ToString();
    }

    static void Repo(OutputWriter w, ProjectModel projects, IndexSnapshot index)
    {
        var sections = new List<Section>();
        if (projects.Solutions.Count > 0)
            sections.Add(Joined("solutions", SolutionsShare, "solutions: ",
                projects.Solutions.Select(s => $"{s.Name} ({Plural(s.ProjectPaths.Count, "project")})").ToList(), projects.Solutions.Count));

        if (projects.Projects.Count > 0)
        {
            var counts = CountsByProject(projects, index);
            sections.Add(new Section("projects", ProjectsShare, "projects (dependency order):",
                projects.Projects.Select(p => ProjectLine(projects, p, counts)).ToList(), projects.Projects.Count, Joined: false,
                More: n => $"… {n} more projects — narrow with scope=<folder>"));
        }

        var packages = projects.Projects
            .SelectMany(p => p.Packages.Select(package => (Project: p, Package: package)))
            .GroupBy(x => x.Package.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First().Package.Name, Count: g.Select(x => x.Project.Path).Distinct().Count(),
                Versions: g.Select(x => x.Package.Version).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList()))
            .OrderByDescending(p => p.Count).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (packages.Count > 0)
            sections.Add(Joined("packages", SectionShare, "packages: ", packages.Take(MaxPackages)
                .Select(p => $"{p.Name}{(p.Versions.Count == 0 ? "" : " " + string.Join("/", p.Versions))} ({p.Count})").ToList(), packages.Count));

        var entries = EntryPoints(index);
        if (entries.Count > 0) sections.Add(Lines("entry points", "entry points:", entries.Take(MaxEntryPoints).ToList(), entries.Count));

        var folders = Folders(index);
        if (folders.Count > 0) sections.Add(Lines("folders", "folders:", folders, folders.Count));

        w.AppendRequired(index.Status.Describe(), OmittedNote(sections.Select(s => s.Name), RepoHint), "the index status");
        Compose(w, sections, RepoHint);
    }

    static void Project(OutputWriter w, ProjectModel projects, IndexSnapshot index, ProjectInfo p)
    {
        var sections = new List<Section>();
        if (p.ProjectReferences.Count > 0)
            sections.Add(Joined("references", SectionShare, "references: ", p.ProjectReferences.Select(r => NameOf(projects, r)).ToList(), p.ProjectReferences.Count));
        var referencedBy = projects.ReferencedBy(p);
        if (referencedBy.Count > 0)
            sections.Add(Joined("referenced by", SectionShare, "referenced by: ", referencedBy.Select(r => r.Name).ToList(), referencedBy.Count));
        if (p.Packages.Count > 0)
            sections.Add(Joined("packages", SectionShare, "packages: ",
                p.Packages.Select(x => x.Version is null ? x.Name : $"{x.Name} {x.Version}").ToList(), p.Packages.Count));
        Area(sections, index.Files.Values.Where(f => projects.ProjectForFile(f.Path) == p).ToList());

        var reserve = OmittedNote(sections.Select(s => s.Name), ScopedHint);
        if (!index.Status.IsReady) w.AppendRequired(index.Status.Describe(), reserve, "the index status");
        var description = p.Error is null ? JoinParts("  ", p.Kind.Label(), Frameworks(p)) : Echo.Cut(p.Error, 2 * Echo.MaxChars);
        w.AppendRequired($"project: {p.Name}  {description}  {p.Path}", reserve, "the project line");
        Compose(w, sections, ScopedHint);
    }

    static void Folder(OutputWriter w, ProjectModel projects, IndexSnapshot index, string folder)
    {
        var sections = new List<Section>();
        var owner = projects.ProjectForFile(folder + "/");
        var contained = projects.Projects
            .Where(p => p != owner && (p.Directory.Equals(folder, StringComparison.OrdinalIgnoreCase)
                || p.Directory.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (contained.Count > 0) sections.Add(Joined("projects", SectionShare, "projects: ", contained.Select(p => p.Name).ToList(), contained.Count));
        Area(sections, index.Files.Values.Where(f => f.Path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)).ToList());

        var reserve = OmittedNote(sections.Select(s => s.Name), ScopedHint);
        if (!index.Status.IsReady) w.AppendRequired(index.Status.Describe(), reserve, "the index status");
        w.AppendRequired(owner is null ? $"folder: {folder}" : $"folder: {folder}  in {owner.Name}", reserve, "the folder line");
        Compose(w, sections, ScopedHint);
    }

    /// <summary>Namespaces, roles, resource dictionaries and largest files for a set of files.</summary>
    static void Area(List<Section> sections, List<FileEntry> files)
    {
        var symbols = files.SelectMany(f => f.Symbols).Where(s => s.Kind != SymbolKind.Namespace).ToList();
        var typeNames = symbols.Where(s => s.Kind.IsType()).Select(s => s.QualifiedName).ToHashSet();
        var topTypes = symbols.Where(s => s.Kind.IsType() && !typeNames.Contains(s.Container)).DistinctBy(s => s.QualifiedName).ToList();

        var namespaces = topTypes.GroupBy(s => s.Container.Length == 0 ? "(global)" : s.Container)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} ({g.Count()})").ToList();
        if (namespaces.Count > 0) sections.Add(Joined("namespaces", SectionShare, "namespaces: ", namespaces, namespaces.Count));

        var roles = topTypes.Select(t => SuffixesLongestFirst.FirstOrDefault(r => t.Name.EndsWith(r, StringComparison.Ordinal)))
            .OfType<string>()
            .GroupBy(r => r)
            .OrderByDescending(g => g.Count()).ThenBy(g => RoleSuffixes.ToList().IndexOf(g.Key))
            .Select(g => $"{g.Key} {g.Count()}")
            .ToList();
        if (roles.Count > 0) sections.Add(Joined("roles", SectionShare, "roles: ", roles, roles.Count));

        var dictionaries = files.Where(f => f.Xaml?.RootElement == "ResourceDictionary")
            .Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (dictionaries.Count > 0) sections.Add(Joined("resource dictionaries", SectionShare, "resource dictionaries: ", dictionaries, dictionaries.Count));

        var largest = files.OrderByDescending(f => f.LineCount).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxLargestFiles).Select(f => $"{f.Path} ({Number(f.LineCount)} lines)").ToList();
        if (largest.Count > 0) sections.Add(Joined("largest files", SectionShare, "largest files: ", largest, largest.Count));
    }

    static List<string> EntryPoints(IndexSnapshot index)
    {
        var entries = new List<string>();
        foreach (var f in index.Files.Values.Where(f => f.Xaml is { } x && x.RootElement.EndsWith("Application", StringComparison.Ordinal))
                     .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var x = f.Xaml!;
            var line = new StringBuilder($"  {f.Path}");
            if (x.XClass is not null) line.Append($"  x:Class={x.XClass}");
            foreach (var name in new[] { "StartupUri", "Startup" })
                if (x.RootAttributes.TryGetValue(name, out var value)) line.Append($"  {name}={value}");
            entries.Add(line.ToString());
        }
        entries.AddRange(index.Symbols
            .Where(s => s.Kind.IsType() && s.Bases.Any(b => b.EndsWith("Application", StringComparison.Ordinal)))
            .Select(s => $"  {s.Path}:{s.StartLine}  {s.Name} : {s.Bases.First(b => b.EndsWith("Application", StringComparison.Ordinal))}"));
        entries.AddRange(index.Symbols
            .Where(s => s.Kind == SymbolKind.Method && s.Name == "Main" && s.Signature.Split(' ').Contains("static"))
            .Select(s => $"  {s.Path}:{s.StartLine}  {s.Container.Split('.')[^1]}.Main"));
        return entries;
    }

    static List<string> Folders(IndexSnapshot index)
    {
        var top = index.Files.Values.Where(f => f.Path.Contains('/'))
            .GroupBy(f => f.Path[..f.Path.IndexOf('/')], StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var lines = new List<string>();
        foreach (var (group, i) in top.Take(MaxTopFolders).Select((g, i) => (g, i)))
        {
            lines.Add($"  {group.Key}/  {Counts(group)}");
            if (i >= ExpandedFolders) continue;
            var subfolders = group.Where(f => f.Path.Count(c => c == '/') >= 2)
                .GroupBy(f => f.Path.Split('/')[1], StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSubfolders);
            lines.AddRange(subfolders.Select(sub => $"    {group.Key}/{sub.Key}/  {Counts(sub)}"));
        }
        if (top.Count > MaxTopFolders) lines.Add($"  … {top.Count - MaxTopFolders} more folders");
        return lines;
    }

    static Dictionary<ProjectInfo, (int Cs, int Xaml)> CountsByProject(ProjectModel projects, IndexSnapshot index)
    {
        var counts = new Dictionary<ProjectInfo, (int Cs, int Xaml)>();
        foreach (var f in index.Files.Values)
        {
            if (projects.ProjectForFile(f.Path) is not { } p) continue;
            var (cs, xaml) = counts.GetValueOrDefault(p);
            counts[p] = f.Kind == FileKind.Xaml ? (cs, xaml + 1) : (cs + 1, xaml);
        }
        return counts;
    }

    static string ProjectLine(ProjectModel projects, ProjectInfo p, Dictionary<ProjectInfo, (int Cs, int Xaml)> counts)
    {
        if (p.Error is not null) return $"  {p.Name}  {p.Error}";
        var references = p.ProjectReferences.Count == 0 ? "" : "→ " + string.Join(", ", p.ProjectReferences.Select(r => NameOf(projects, r)));
        var (cs, xaml) = counts.GetValueOrDefault(p);
        return "  " + JoinParts("  ", p.Name, p.Kind.Label(), Frameworks(p), references, $"[{cs} cs, {xaml} xaml]");
    }

    static string Frameworks(ProjectInfo p) =>
        p.TargetFrameworks.Count == 0 ? "" : string.Join("/", p.TargetFrameworks) + (p.Approximate ? "~" : "");

    static string NameOf(ProjectModel projects, string projectPath) =>
        projects.Projects.FirstOrDefault(x => x.Path.Equals(projectPath, StringComparison.OrdinalIgnoreCase))?.Name
        ?? Path.GetFileNameWithoutExtension(projectPath);

    static string? FolderOf(Workspace ws, string scope)
    {
        try
        {
            var full = ws.Root.Resolve(scope);
            if (!Directory.Exists(full)) return null;
            var relative = ws.Root.ToRelative(full);
            return relative == "." ? "" : relative;
        }
        catch (WpfNavException)
        {
            return null;
        }
    }

    /// <summary>
    /// One overview section. Joined: a single line "Header item, item, … N more". Otherwise the header on its own line, one
    /// line per item, then <see cref="More"/> for the items left out. <see cref="Total"/> may exceed the items offered.
    /// </summary>
    sealed record Section(string Name, double Share, string Header, IReadOnlyList<string> Items, int Total, bool Joined, Func<int, string> More);

    static Section Joined(string name, double share, string prefix, IReadOnlyList<string> items, int total) =>
        new(name, share, prefix, items, total, Joined: true, More: n => $"… {n} more");

    static Section Lines(string name, string header, IReadOnlyList<string> lines, int total) =>
        new(name, SectionShare, header, lines, total, Joined: false, More: n => $"  … {n} more");

    static string? OmittedNote(IEnumerable<string> names, string hint) =>
        string.Join(", ", names) is { Length: > 0 } list ? $"omitted: {list} — {hint}" : null;

    /// <summary>
    /// Writes the sections in order within one budget. Before each section, room stays reserved for the note naming every
    /// section that could still be left out, so a section that does not fit is labelled instead of silently dropped.
    /// The caller wrote its identity lines with the note for all sections reserved.
    /// </summary>
    static void Compose(OutputWriter w, IReadOnlyList<Section> sections, string hint)
    {
        var omitted = new List<string>();
        for (var i = 0; i < sections.Count; i++)
        {
            var reserve = OmittedNote(omitted.Concat(sections.Skip(i + 1).Select(s => s.Name)), hint);
            var quota = OutputWriter.CapacityOf((int)(w.Limit * sections[i].Share));
            var lines = Render(sections[i], Math.Min(quota, w.Room(reserve)));
            if (lines is null || !w.TryAppendBlock(lines, reserve)) omitted.Add(sections[i].Name);
        }
        if (OmittedNote(omitted, hint) is { } note) w.AppendReserved(note);
    }

    /// <summary>The section's lines within <paramref name="room"/> characters (newlines included), or null when not even its header and count fit.</summary>
    static List<string>? Render(Section s, int room)
    {
        if (s.Joined)
        {
            var line = new StringBuilder(s.Header);
            var shown = 0;
            foreach (var item in s.Items)
            {
                var next = (shown == 0 ? "" : ", ") + item;
                var left = s.Total - shown - 1;
                var tail = left > 0 ? ", " + s.More(left) : "";
                if (line.Length + next.Length + tail.Length + 1 > room) break;
                line.Append(next);
                shown++;
            }
            if (shown < s.Total) line.Append(shown == 0 ? "" : ", ").Append(s.More(s.Total - shown));
            return line.Length + 1 <= room ? [line.ToString()] : null;
        }

        var lines = new List<string> { s.Header };
        var used = s.Header.Length + 1;
        foreach (var item in s.Items)
        {
            var left = s.Total - lines.Count;
            var more = left > 0 ? s.More(left).Length + 1 : 0;
            if (used + item.Length + 1 + more > room) break;
            lines.Add(item);
            used += item.Length + 1;
        }
        var cut = s.Total - (lines.Count - 1);
        if (cut > 0)
        {
            lines.Add(s.More(cut));
            used += lines[^1].Length + 1;
        }
        return used <= room ? lines : null;
    }

    static string JoinParts(string separator, params string[] parts) => string.Join(separator, parts.Where(p => p.Length > 0));

    static string Counts(IEnumerable<FileEntry> files)
    {
        var list = files.ToList();
        var xaml = list.Count(f => f.Kind == FileKind.Xaml);
        return $"{list.Count - xaml} cs, {xaml} xaml";
    }

    static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    static string Number(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
