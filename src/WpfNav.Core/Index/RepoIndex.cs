using System.Collections.Concurrent;
using WpfNav.Core.Inventory;
using WpfNav.Core.Model;
using WpfNav.Core.Parsing;
using WpfNav.Core.Projects;

namespace WpfNav.Core.Index;

/// <summary>The in-memory symbol index. Readers take <see cref="Current"/>, which is never modified once published.</summary>
public sealed class RepoIndex(RepoRoot root)
{
    const int PublishEvery = 500;

    readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object _gate = new();
    volatile IndexSnapshot _current = IndexSnapshot.Empty;
    int _publishedParsed = -1;
    ProjectModel _projects = ProjectModel.Empty;

    public IndexSnapshot Current => _current;

    /// <summary>Completes when the first build has finished.</summary>
    public Task Ready => _ready.Task;

    /// <summary>
    /// Parses every .cs/.xaml file in parallel. The first build publishes partial snapshots as it goes; a rebuild keeps the
    /// previous snapshot visible and publishes once at the end (callers rescan afterwards to pick up edits made meanwhile).
    /// </summary>
    public async Task BuildAsync(ProjectModel projects, IReadOnlyList<InventoryFile> files, CancellationToken ct)
    {
        var sources = files.Where(f => f.Kind is FileKind.CSharp or FileKind.Xaml).ToList();
        var entries = new ConcurrentDictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var first = !_ready.Task.IsCompleted;
        var parsed = 0;
        lock (_gate)
        {
            _projects = projects;
            _publishedParsed = -1;
        }
        if (first) Publish(entries, new IndexStatus(false, 0, sources.Count));
        await Parallel.ForEachAsync(sources, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            (file, _) =>
            {
                if (ParseFile(root, file, projects) is { } entry) entries[entry.Path] = entry;
                var done = Interlocked.Increment(ref parsed);
                if (first && done % PublishEvery == 0) Publish(entries, new IndexStatus(false, done, sources.Count));
                return ValueTask.CompletedTask;
            });
        Publish(entries, new IndexStatus(true, sources.Count, sources.Count));
        _ready.TrySetResult();
    }

    /// <summary>The current snapshot once the first build is done, or after the timeout, whichever comes first.</summary>
    public async Task<IndexSnapshot> WaitForReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (!_ready.Task.IsCompleted && timeout > TimeSpan.Zero)
            await Task.WhenAny(_ready.Task, Task.Delay(timeout, ct));
        ct.ThrowIfCancellationRequested();
        return _current;
    }

    /// <summary>Re-parses the given tracked .cs/.xaml paths that exist, drops the ones that are gone, and publishes a new snapshot.</summary>
    public IndexSnapshot Update(IEnumerable<string> relativePaths)
    {
        lock (_gate)
        {
            var files = new Dictionary<string, FileEntry>(_current.Files, StringComparer.OrdinalIgnoreCase);
            foreach (var path in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase)) Refresh(files, path);
            return PublishChanges(files);
        }
    }

    /// <summary>Re-parses files whose size or timestamp changed or that are new, and drops entries not in the list.</summary>
    public Task<IndexSnapshot> RescanAsync(IReadOnlyList<InventoryFile> files, CancellationToken ct) => Task.Run(() =>
    {
        lock (_gate)
        {
            var wanted = files.Where(f => f.Kind is FileKind.CSharp or FileKind.Xaml)
                .GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var next = new Dictionary<string, FileEntry>(_current.Files.Where(kv => wanted.ContainsKey(kv.Key)), StringComparer.OrdinalIgnoreCase);
            foreach (var file in wanted.Values)
            {
                ct.ThrowIfCancellationRequested();
                if (next.TryGetValue(file.RelativePath, out var known) && known.Length == file.Length && known.LastWriteUtc == file.LastWriteUtc) continue;
                if (ParseFile(root, file, _projects) is { } entry) next[entry.Path] = entry;
                else next.Remove(file.RelativePath);
            }
            return PublishChanges(next);
        }
    }, ct);

    /// <summary>Checks the indexed paths against the disk and re-parses stale ones; true when anything changed.</summary>
    public bool EnsureFresh(IEnumerable<string> relativePaths)
    {
        var current = _current;
        var stale = new List<string>();
        foreach (var path in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!current.Files.TryGetValue(path, out var entry)) continue;
            var info = new FileInfo(root.Resolve(path));
            if (!info.Exists || info.Length != entry.Length || info.LastWriteTimeUtc != entry.LastWriteUtc) stale.Add(path);
        }
        if (stale.Count == 0) return false;
        Update(stale);
        return true;
    }

    /// <summary>Parses one file into an entry, or null when it vanished or cannot be read.</summary>
    public static FileEntry? ParseFile(RepoRoot root, InventoryFile file, ProjectModel projects)
    {
        string text;
        FileInfo info;
        try
        {
            info = new FileInfo(file.FullPath);
            if (!info.Exists) return null;
            text = File.ReadAllText(file.FullPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var project = projects.ProjectForFile(file.RelativePath);
        var inTest = project?.Kind == ProjectKind.Test;
        IReadOnlyList<SymbolRecord> symbols;
        XamlRootInfo? xaml = null;
        int lineCount;
        if (file.Kind == FileKind.Xaml)
        {
            var outline = XamlOutliner.Parse(text, file.RelativePath);
            (symbols, lineCount) = (outline.Symbols, outline.LineCount);
            xaml = new XamlRootInfo(outline.RootElement, outline.XClass, outline.RootAttributes);
        }
        else
        {
            var outline = CSharpOutliner.Parse(text, file.RelativePath);
            (symbols, lineCount) = (outline.AllSymbols().ToList(), outline.LineCount);
        }

        var tagged = symbols.Select(s => s with { Project = project?.Name, InTestProject = inTest }).ToList();
        return new FileEntry(file.RelativePath, file.Kind, info.Length, info.LastWriteTimeUtc, lineCount, project?.Name, inTest, tagged, xaml);
    }

    void Refresh(Dictionary<string, FileEntry> files, string path)
    {
        var kind = FileInventory.KindOf(path);
        if (kind is not (FileKind.CSharp or FileKind.Xaml)) return;
        string full;
        try { full = root.Resolve(path); }
        catch (WpfNavException) { return; }
        var relative = root.ToRelative(full);
        if (ParseFile(root, new InventoryFile(relative, full, kind.Value, 0, default), _projects) is { } entry) files[relative] = entry;
        else files.Remove(relative);
    }

    IndexSnapshot PublishChanges(Dictionary<string, FileEntry> files)
    {
        var status = _current.Status.IsReady ? new IndexStatus(true, files.Count, files.Count) : _current.Status;
        _current = new IndexSnapshot(files, status);
        return _current;
    }

    void Publish(ConcurrentDictionary<string, FileEntry> entries, IndexStatus status)
    {
        lock (_gate)
        {
            if (!status.IsReady && status.Parsed <= _publishedParsed) return;
            _publishedParsed = status.Parsed;
            _current = new IndexSnapshot(new Dictionary<string, FileEntry>(entries, StringComparer.OrdinalIgnoreCase), status);
        }
    }
}
