using WpfNav.Core.Inventory;

namespace WpfNav.Core.Index;

/// <summary>Keeps the index current: a file watcher feeds changed paths, applied in batches after a short quiet period.</summary>
public sealed class IndexUpdater : IDisposable
{
    readonly Workspace _ws;
    readonly TimeSpan _debounce;
    readonly object _gate = new();
    readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly Timer _timer;
    FileSystemWatcher? _watcher;
    bool _rescan;

    public IndexUpdater(Workspace ws, TimeSpan debounce)
    {
        _ws = ws;
        _debounce = debounce;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _watcher = new FileSystemWatcher(_ws.Root.FullPath)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 65536,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) => OnCreated(e.FullPath);
        _watcher.Changed += (_, e) => OnChanged(e.FullPath);
        _watcher.Deleted += (_, e) => OnDeleted(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            OnDeleted(e.OldFullPath);
            OnCreated(e.FullPath);
        };
        _watcher.Error += (_, _) => _ = OnOverflowAsync();
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _timer.Dispose();
    }

    /// <summary>The watcher lost events: compare every file's size and timestamp with the index.</summary>
    internal async Task OnOverflowAsync()
    {
        await _ws.Index.Ready;
        await _ws.Index.RescanAsync(_ws.Inventory.Enumerate(), CancellationToken.None);
    }

    void OnCreated(string fullPath)
    {
        if (Relative(fullPath) is not { } relative) return;
        if (Directory.Exists(fullPath)) Queue(rescan: true);
        else if (_ws.Inventory.IsTracked(relative)) Queue(relative);
    }

    void OnChanged(string fullPath)
    {
        if (Relative(fullPath) is { } relative && !Directory.Exists(fullPath) && _ws.Inventory.IsTracked(relative)) Queue(relative);
    }

    void OnDeleted(string fullPath)
    {
        if (Relative(fullPath) is not { } relative) return;
        if (_ws.Inventory.IsTracked(relative)) Queue(relative);
        else if (_ws.Index.Current.Files.Keys.Any(k => k.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase))) Queue(rescan: true);
    }

    /// <summary>Repo-relative path, or null for paths outside the root or under skipped folders (bin, obj, .git…).</summary>
    string? Relative(string fullPath)
    {
        try
        {
            var relative = _ws.Root.ToRelative(fullPath);
            return relative.Split('/').Any(FileInventory.SkippedDirectories.Contains) ? null : relative;
        }
        catch (WpfNavException)
        {
            return null;
        }
    }

    void Queue(string? path = null, bool rescan = false)
    {
        lock (_gate)
        {
            if (path is not null) _pending.Add(path);
            _rescan |= rescan;
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    void Flush()
    {
        List<string> batch;
        bool rescan;
        lock (_gate)
        {
            batch = _pending.ToList();
            _pending.Clear();
            rescan = _rescan;
            _rescan = false;
        }
        _ = ApplyAsync(batch, rescan);
    }

    async Task ApplyAsync(List<string> batch, bool rescan)
    {
        try
        {
            await _ws.Index.Ready; // events that arrive during the first build wait for it
            if (batch.Any(p => FileInventory.KindOf(p) is FileKind.Project or FileKind.Solution or FileKind.Props or FileKind.PackagesConfig))
            {
                await _ws.ReloadProjectsAsync(); // a full rebuild also covers the other files in the batch
                return;
            }
            if (rescan) await _ws.Index.RescanAsync(_ws.Inventory.Enumerate(), CancellationToken.None);
            if (batch.Count > 0) _ws.Index.Update(batch);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or WpfNavException)
        {
            // The next change, a rescan or a stale-result check will catch up.
        }
    }
}
