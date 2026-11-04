namespace WpfNav.Tests.TestSupport;

public static class TestWorkspace
{
    /// <summary>A workspace over the repo with indexing already started (no file watcher; tests write files directly).</summary>
    public static Workspace Started(TempRepo repo)
    {
        var ws = new Workspace(new RepoRoot(repo.Root), [], watch: false);
        ws.Start();
        return ws;
    }
}
