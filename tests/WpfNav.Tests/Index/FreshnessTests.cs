namespace WpfNav.Tests.Index;

public class FreshnessTests
{
    [Fact]
    public async Task Created_changed_and_deleted_files_reach_the_index_within_a_second()
    {
        using var repo = TempRepo.Create(("src/A.cs", "class A { }"));
        using var ws = new Workspace(new RepoRoot(repo.Root), []);
        ws.Start();
        await ws.GetIndexAsync(default);
        repo.Write("src/B.cs", "class Added { }");
        await Eventually.True(() => ws.Index.Current.Symbols.Any(s => s.Name == "Added"), 1000);
        repo.Write("src/B.cs", "class Renamed { }");
        await Eventually.True(() => ws.Index.Current.Symbols.Any(s => s.Name == "Renamed") && ws.Index.Current.Symbols.All(s => s.Name != "Added"), 1000);
        repo.Delete("src/B.cs");
        await Eventually.True(() => !ws.Index.Current.Files.ContainsKey("src/B.cs"), 1000);
    }

    [Fact]
    public async Task Rescan_catches_changes_the_watcher_missed()
    {
        using var repo = TempRepo.Create(("src/A.cs", "class A { }"), ("src/Old.cs", "class Old { }"));
        using var ws = new Workspace(new RepoRoot(repo.Root), [], watch: false);
        ws.Start();
        await ws.GetIndexAsync(default);
        repo.Write("src/A.cs", "class A2 { }");
        repo.Write("src/New.cs", "class New { }");
        repo.Delete("src/Old.cs");
        var snap = await ws.Index.RescanAsync(ws.Inventory.Enumerate(), default);
        Assert.Equal(new[] { "A2", "New" }, snap.Symbols.Select(s => s.Name).Order());
    }

    [Fact]
    public async Task Stale_result_files_are_reparsed_before_answering()
    {
        using var repo = TempRepo.Create(("src/A.cs", "class A\n{\n    void Run() { }\n}\n"));
        using var ws = new Workspace(new RepoRoot(repo.Root), [], watch: false);
        ws.Start();
        await ws.GetIndexAsync(default);
        repo.Write("src/A.cs", "// one\n// two\nclass A\n{\n    void Run() { }\n}\n");
        Assert.Contains("src/A.cs:5", await SymbolFinder.FindAsync(ws, new SymbolQuery("Run"), 20, 0, default));
    }

    [Fact]
    public async Task Project_file_changes_reload_the_project_model()
    {
        using var repo = TempRepo.Create(("src/A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"));
        using var ws = new Workspace(new RepoRoot(repo.Root), []);
        ws.Start();
        await ws.GetIndexAsync(default);
        repo.Write("src/B/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await Eventually.TrueAsync(async () => (await ws.GetProjectsAsync(default)).FindByName("B") is not null, 2000);
    }

    [Fact]
    public async Task An_edit_during_a_project_reload_survives_it()
    {
        using var repo = ManyFiles(4000);
        using var ws = new Workspace(new RepoRoot(repo.Root), [], watch: false);
        ws.Start();
        await ws.GetIndexAsync(default);
        var reload = ws.ReloadProjectsAsync();
        var n = 0;
        do
        {
            repo.Write("src/C0000.cs", $"class Edit{++n} {{ }}");
            ws.Index.Update(new[] { "src/C0000.cs" }); // what the watcher does after each edit
            await Task.Delay(10);
        }
        while (!reload.IsCompleted);
        await reload;
        Assert.Contains(ws.Index.Current.Symbols, s => s.Name == $"Edit{n}");
    }

    [Fact]
    public async Task A_project_reload_keeps_the_index_populated()
    {
        using var repo = ManyFiles(2000);
        using var ws = new Workspace(new RepoRoot(repo.Root), [], watch: false);
        ws.Start();
        await ws.GetIndexAsync(default);
        var reload = ws.ReloadProjectsAsync();
        var smallest = ws.Index.Current.Files.Count;
        while (!reload.IsCompleted)
        {
            smallest = Math.Min(smallest, ws.Index.Current.Files.Count);
            await Task.Delay(1);
        }
        await reload;
        Assert.Equal(2000, smallest);
        Assert.True(ws.Index.Current.Status.IsReady);
    }

    [Fact]
    public void Start_survives_a_watcher_that_cannot_start()
    {
        var repo = TempRepo.Create(("src/A.cs", "class A { }"));
        var ws = new Workspace(new RepoRoot(repo.Root), []);
        repo.Dispose(); // the root disappears, so the file watcher cannot be created
        ws.Start();
        ws.Dispose();
    }

    static TempRepo ManyFiles(int count) => TempRepo.Create(Enumerable.Range(0, count)
        .Select(i => ($"src/C{i:0000}.cs", $"class C{i:0000} {{ void M() {{ }} }}"))
        .Append(("src/P.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"))
        .ToArray());

    [Fact]
    public async Task Snapshots_never_change_after_publication()
    {
        using var repo = TempRepo.Create(("src/A.cs", "class A { }"));
        using var ws = new Workspace(new RepoRoot(repo.Root), [], watch: false);
        ws.Start();
        var before = await ws.GetIndexAsync(default);
        repo.Write("src/B.cs", "class B { }");
        ws.Index.Update(new[] { "src/B.cs" });
        Assert.Single(before.Files);
        Assert.Equal(2, ws.Index.Current.Files.Count);
    }
}
