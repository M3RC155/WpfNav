using WpfNav.Tests.Parsing;

namespace WpfNav.Tests.Index;

public class RepoIndexTests
{
    [Fact]
    public void Status_lines()
    {
        Assert.Equal("index 63% (5,300/8,400 files) — results may be incomplete", new IndexStatus(false, 5300, 8400).Describe());
        Assert.Equal("index: ready (8,412 files)", new IndexStatus(true, 8412, 8412).Describe());
    }

    [Fact]
    public async Task Builds_symbols_with_projects_and_skips_vanished_files()
    {
        using var repo = TempRepo.Create(
            ("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWPF>true</UseWPF></PropertyGroup></Project>"),
            ("src/App/A.cs", "namespace App;\npublic class A\n{\n    public void Run() { }\n}\n"),
            ("src/App/V.xaml", XamlOutlinerTests.View),
            ("tests/T/T.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup></Project>"),
            ("tests/T/ATests.cs", "public class ATests { }"));
        repo.Write("src/App/U.cs", "public class Ünïcode { }", Encoding.Unicode);
        var root = new RepoRoot(repo.Root);
        var files = new FileInventory(root, []).Enumerate().ToList();
        files.Add(new InventoryFile("src/App/Gone.cs", Path.Combine(repo.Root, "src", "App", "Gone.cs"), FileKind.CSharp, 10, DateTime.UtcNow));
        var index = new RepoIndex(root);
        await index.BuildAsync(await ProjectModel.LoadAsync(root, files, default), files, default);
        var snap = index.Current;
        Assert.Equal((true, 5, 5), (snap.Status.IsReady, snap.Status.Parsed, snap.Status.Total));
        Assert.False(snap.Files.ContainsKey("src/App/Gone.cs"));
        var run = snap.Symbols.Single(s => s.Name == "Run");
        Assert.Equal(("App", false), (run.Project, run.InTestProject));
        Assert.True(snap.Symbols.Single(s => s.Name == "ATests").InTestProject);
        Assert.Contains(snap.Symbols, s => s.Name == "Ünïcode");
        Assert.Contains(snap.Symbols, s => s.Kind == SymbolKind.XamlResource && s.Name == "OrderLineTemplate");
        Assert.DoesNotContain(snap.Symbols, s => s.Kind == SymbolKind.Namespace);
        Assert.Equal("UserControl", snap.Files["src/App/V.xaml"].Xaml!.RootElement);
    }

    [Fact]
    public async Task Workspace_builds_in_the_background_and_bounds_the_wait()
    {
        using var repo = TempRepo.Create(("src/A.cs", "public class A { }"));
        using var idle = new Workspace(new RepoRoot(repo.Root), [], indexWait: TimeSpan.Zero);
        Assert.False((await idle.GetIndexAsync(default)).Status.IsReady);
        using var ws = new Workspace(new RepoRoot(repo.Root), []);
        ws.Start();
        var snap = await ws.GetIndexAsync(default);
        Assert.True(snap.Status.IsReady);
        Assert.Contains(snap.Symbols, s => s.Name == "A");
    }
}
