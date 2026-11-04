using WpfNav.Core.Index;
using WpfNav.Core.Inventory;
using WpfNav.Core.Projects;

namespace WpfNav.Core;

/// <summary>Everything the tools share about one repository: root, file inventory, project model and symbol index.</summary>
public sealed class Workspace : IDisposable
{
    readonly TimeSpan _indexWait;
    readonly bool _watch;
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _reload = new(1, 1);
    readonly TaskCompletionSource _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object _gate = new();
    Task<ProjectModel>? _projects;
    Task? _background;
    IndexUpdater? _updater;
    Exception? _failure;

    /// <param name="indexWait">How long index-backed tools wait for the first build before answering with partial results.</param>
    /// <param name="watch">Keep the index current with a file watcher once started.</param>
    public Workspace(RepoRoot root, IReadOnlyList<string> excludeGlobs, TimeSpan? indexWait = null, bool watch = true)
    {
        Root = root;
        Inventory = new FileInventory(root, excludeGlobs);
        Index = new RepoIndex(root);
        _indexWait = indexWait ?? TimeSpan.FromSeconds(10);
        _watch = watch;
    }

    public RepoRoot Root { get; }

    public FileInventory Inventory { get; }

    public RepoIndex Index { get; }

    /// <summary>Starts the watcher (when enabled), then loads projects and builds the index in the background. Safe to call more than once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_background is not null) return;
            if (_watch)
            {
                var updater = new IndexUpdater(this, TimeSpan.FromMilliseconds(300));
                try
                {
                    updater.Start();
                    _updater = updater;
                }
                catch (Exception e) when (e is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
                {
                    updater.Dispose(); // this drive can't be watched; results are still re-checked against the disk
                }
            }
            _background = Task.Run(async () =>
            {
                try
                {
                    var files = Inventory.Enumerate();
                    var projects = await LoadProjects(files);
                    await Index.BuildAsync(projects, files, _stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    _failure = e;
                    _failed.TrySetResult();
                }
            });
        }
    }

    public async Task<ProjectModel> GetProjectsAsync(CancellationToken ct)
    {
        try
        {
            return await LoadProjects(null).WaitAsync(ct);
        }
        catch (Exception e) when (e is not (OperationCanceledException or WpfNavException))
        {
            throw new WpfNavException($"Indexing failed: {e.Message}");
        }
    }

    public async Task<IndexSnapshot> GetIndexAsync(CancellationToken ct)
    {
        var wait = Index.WaitForReadyAsync(_indexWait, ct);
        await Task.WhenAny(wait, _failed.Task); // a failed build answers at once instead of after the full wait
        if (_failure is not null) throw new WpfNavException($"Indexing failed: {_failure.Message}");
        return await wait;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _updater?.Dispose();
    }

    /// <summary>Reloads the project model and rebuilds the index, after a project, solution or props file changed.</summary>
    internal async Task ReloadProjectsAsync()
    {
        await _reload.WaitAsync(_stop.Token);
        try
        {
            var files = Inventory.Enumerate();
            var projects = Task.Run(() => ProjectModel.LoadAsync(Root, files, _stop.Token));
            lock (_gate) _projects = projects;
            await Index.BuildAsync(await projects, files, _stop.Token);
            // The rebuild parsed each file once; anything edited after that is caught by comparing sizes and timestamps again.
            await Index.RescanAsync(Inventory.Enumerate(), _stop.Token);
            _failure = null;
        }
        finally
        {
            _reload.Release();
        }
    }

    Task<ProjectModel> LoadProjects(IReadOnlyList<InventoryFile>? files)
    {
        lock (_gate)
            return _projects ??= Task.Run(() => ProjectModel.LoadAsync(Root, files ?? Inventory.Enumerate(), _stop.Token));
    }
}
