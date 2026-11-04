namespace WpfNav.Tests.Navigation;

public class OverviewBuilderTests
{
    [Fact]
    public async Task Repo_scope_maps_solutions_projects_packages_entry_points_and_folders()
    {
        using var repo = SampleRepo.Create();
        using var ws = TestWorkspace.Started(repo);
        var text = await OverviewBuilder.BuildAsync(ws, null, default);
        Assert.StartsWith(TestText.Lf($"root: {repo.Root}\nindex: ready (11 files)\nsolutions: All.slnx (4 projects)\nprojects (dependency order):\n"), text);
        Assert.Contains(TestText.Lf("""
              Gen  app  net8.0  [1 cs, 0 xaml]
              Shell  WPF app  net8.0-windows  → Core, Orders  [2 cs, 1 xaml]
              Orders  WPF library  net8.0-windows  → Core  [3 cs, 2 xaml]
              Core.Tests  test  → Core  [1 cs, 0 xaml]
              Core  library  net8.0  [1 cs, 0 xaml]
            packages: CommunityToolkit.Mvvm 8.2.2 (1), xunit 2.9.3 (1)
            entry points:
              src/Shell/App.xaml  x:Class=Shell.App  StartupUri=MainWindow.xaml
              src/Shell/App.xaml.cs:2  App : Application
              tools/Gen/Program.cs:3  Program.Main
            folders:
              src/  6 cs, 3 xaml
            """), text);
    }

    [Fact]
    public async Task Project_and_folder_scopes_summarize_one_area()
    {
        using var repo = SampleRepo.Create();
        using var ws = TestWorkspace.Started(repo);
        Assert.Contains(TestText.Lf("""
            project: Orders  WPF library  net8.0-windows  src/Orders/Orders.csproj
            references: Core
            referenced by: Shell
            namespaces: Orders (1), Orders.ViewModels (1), Orders.Views (1)
            roles: ViewModel 2, View 1
            resource dictionaries: src/Orders/Themes/Generic.xaml
            largest files: src/Orders/Views/OrderEditView.xaml (28 lines), src/Orders/ViewModels/OrderEditViewModel.cs (13 lines)
            """), await OverviewBuilder.BuildAsync(ws, "Orders", default));
        Assert.Contains(TestText.Lf("folder: src/Orders/ViewModels  in Orders\nnamespaces: Orders.ViewModels (1)\nroles: ViewModel 1\n"),
            await OverviewBuilder.BuildAsync(ws, "src/Orders/ViewModels", default));
    }

    [Fact]
    public async Task Scoped_overviews_say_when_the_index_is_incomplete()
    {
        using var repo = TempRepo.Create(("src/A.cs", "class A { }"));
        using var idle = new Workspace(new RepoRoot(repo.Root), [], indexWait: TimeSpan.Zero, watch: false);
        Assert.Contains("results may be incomplete", await OverviewBuilder.BuildAsync(idle, "src", default));
    }

    [Fact]
    public async Task Unknown_scopes_broken_projects_and_big_repos_stay_actionable()
    {
        using var repo = SampleRepo.Create();
        using var ws = TestWorkspace.Started(repo);
        Assert.Equal("Unknown scope 'Ordrs'. Pass a project name or a folder. Closest projects: Orders",
            (await Assert.ThrowsAsync<WpfNavException>(() => OverviewBuilder.BuildAsync(ws, "Ordrs", default))).Message);
        using var bad = TempRepo.Create(("broken/Bad.csproj", "<Project"));
        using var badWs = TestWorkspace.Started(bad);
        Assert.Contains("  Bad  not parsed: ", await OverviewBuilder.BuildAsync(badWs, null, default));
        using var big = TempRepo.Create(Enumerable.Range(0, 200).Select(i => ($"src/P{i:000}/P{i:000}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />")).ToArray());
        using var bigWs = TestWorkspace.Started(big);
        var text = await OverviewBuilder.BuildAsync(bigWs, null, default);
        Assert.True(OutputWriter.EstimateTokens(text) <= Limits.Overview);
        Assert.Matches(@"… \d+ more projects — narrow with scope=<folder>\n", text);
    }

    const string LongFolder = "a-deliberately-long-repository-folder-name-that-makes-the-root-line-take-a-good-share-of-a-small-budget-0123456789-abcdefghij";

    /// <summary>150 projects with packages and references, 40 application entry points and Main methods, under a long root.</summary>
    static (TempRepo Repo, string Root) Big(int depth = 1)
    {
        var at = string.Join("/", Enumerable.Repeat(LongFolder, depth));
        var files = new List<(string, string)>
        {
            ($"{at}/All.slnx", "<Solution>" + string.Concat(Enumerable.Range(0, 150).Select(i => $"<Project Path=\"src/P{i:000}/P{i:000}.csproj\" />")) + "</Solution>"),
        };
        for (var i = 0; i < 150; i++)
        {
            var refs = i == 0 ? "" : $"<ProjectReference Include=\"..\\P{i - 1:000}\\P{i - 1:000}.csproj\" />";
            files.Add(($"{at}/src/P{i:000}/P{i:000}.csproj",
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup>{refs}<PackageReference Include=\"Package.Number{i % 30:00}\" Version=\"1.{i}.0\" /></ItemGroup></Project>"));
            files.Add(($"{at}/src/P{i:000}/Services/Service{i:000}.cs", $"namespace P{i:000}.Services;\npublic class Service{i:000} {{ }}\n"));
            if (i < 40)
            {
                files.Add(($"{at}/src/P{i:000}/App.xaml", $"<Application x:Class=\"P{i:000}.App\" StartupUri=\"MainWindow.xaml\" xmlns=\"p\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" />"));
                files.Add(($"{at}/src/P{i:000}/Program.cs", "class Program\n{\n    static void Main() { }\n}\n"));
            }
        }
        var repo = TempRepo.Create(files.ToArray());
        return (repo, Path.Combine(repo.Root, at.Replace('/', Path.DirectorySeparatorChar)));
    }

    static readonly string[] RepoSections = ["solutions", "projects", "packages", "entry points", "folders"];

    [Theory, InlineData(256), InlineData(1000), InlineData(null)]
    public async Task Every_budget_bounds_the_whole_overview_and_keeps_identity_and_projects_first(int? budget)
    {
        var (repo, root) = Big();
        using var _ = repo;
        using var ws = new Workspace(new RepoRoot(root), [], watch: false);
        ws.Start();
        var text = await OverviewBuilder.BuildAsync(ws, null, budget, default);
        Assert.True(OutputWriter.EstimateTokens(text) <= (budget ?? Limits.Overview), $"{OutputWriter.EstimateTokens(text)} tokens");
        Assert.StartsWith(TestText.Lf($"root: {root}\nindex: ready (230 files)\nsolutions: All.slnx (150 projects)\nprojects (dependency order):\n"), text);
        Assert.Matches(@"\n… \d+ more projects — narrow with scope=<folder>\n", text);
        Assert.All(RepoSections, name => Assert.Contains($"\n{name}", text));
        if (budget is null) Assert.Contains("\nentry points:\n  src/P000/App.xaml  x:Class=P000.App  StartupUri=MainWindow.xaml\n", text);
    }

    [Fact]
    public async Task Small_budgets_label_the_sections_they_leave_out()
    {
        var (repo, root) = Big(depth: 3);
        using var _ = repo;
        using var ws = new Workspace(new RepoRoot(root), [], watch: false);
        ws.Start();
        var text = await OverviewBuilder.BuildAsync(ws, null, Budget.Minimum, default);
        Assert.True(OutputWriter.EstimateTokens(text) <= Budget.Minimum);
        Assert.Contains("\nprojects (dependency order):\n", text);
        var omitted = Regex.Match(text, @"\nomitted: (.+) — pass scope=<project or folder> for detail\n$").Groups[1].Value.Split(", ");
        Assert.Contains("folders", omitted);
        Assert.All(RepoSections, name => Assert.True(text.Contains($"\n{name}") != omitted.Contains(name), name));
    }

    [Theory, InlineData(256), InlineData(null)]
    public async Task Project_scope_with_many_references_and_packages_stays_within_the_budget(int? budget)
    {
        var (repo, root) = Big();
        using var _ = repo;
        using var ws = new Workspace(new RepoRoot(root), [], watch: false);
        ws.Start();
        var text = await OverviewBuilder.BuildAsync(ws, "P149", budget, default);
        Assert.True(OutputWriter.EstimateTokens(text) <= (budget ?? Limits.Overview));
        Assert.Contains("\nproject: P149  library  net8.0  src/P149/P149.csproj\nreferences: P148\n", text);
    }

    [Fact]
    public async Task Budgets_outside_the_range_are_rejected()
    {
        using var repo = SampleRepo.Create();
        using var ws = TestWorkspace.Started(repo);
        Assert.Equal("max_tokens must be between 256 and 2500; got 3000. Omit it for the default (2500).",
            (await Assert.ThrowsAsync<WpfNavException>(() => OverviewBuilder.BuildAsync(ws, null, 3000, default))).Message);
        Assert.StartsWith("Unknown scope 'sss",
            (await Assert.ThrowsAsync<WpfNavException>(() => OverviewBuilder.BuildAsync(ws, new string('s', 3000), default))).Message);
    }
}
