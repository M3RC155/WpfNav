namespace WpfNav.Tests.Inventory;

public class FileInventoryTests
{
    static TempRepo Sample() => TempRepo.Create(
        ("All.slnx", "<Solution />"), ("Directory.Build.props", "<Project />"), ("README.md", "x"),
        ("src/App/App.csproj", "<Project />"), ("src/App/packages.config", "<packages />"),
        ("src/App/MainWindow.xaml", "<Window />"), ("src/App/MainWindow.xaml.cs", "class W { }"),
        ("src/App/bin/Debug/Gen.cs", ""), ("src/App/OBJ/X.g.cs", ""), (".git/hooks/a.cs", ""),
        ("node_modules/m/b.cs", ""), ("legacy/Old.cs", ""));

    [Fact]
    public void Lists_tracked_kinds_and_skips_build_and_vcs_folders()
    {
        using var repo = Sample();
        var files = new FileInventory(new RepoRoot(repo.Root), []).Enumerate();
        Assert.Equal(new[] { "All.slnx", "Directory.Build.props", "legacy/Old.cs", "src/App/App.csproj",
                             "src/App/MainWindow.xaml", "src/App/MainWindow.xaml.cs", "src/App/packages.config" },
            files.Select(f => f.RelativePath).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(FileKind.PackagesConfig, files.Single(f => f.RelativePath.EndsWith("packages.config")).Kind);
    }

    [Fact]
    public void Exclude_globs_and_folder_scoping_apply()
    {
        using var repo = Sample();
        var inv = new FileInventory(new RepoRoot(repo.Root), ["legacy/**"]);
        Assert.DoesNotContain(inv.Enumerate(), f => f.RelativePath == "legacy/Old.cs");
        Assert.False(inv.IsTracked("legacy/Old.cs"));
        Assert.Equal(new[] { "src/App/MainWindow.xaml", "src/App/MainWindow.xaml.cs" },
            inv.Enumerate("src/App").Where(f => f.Kind is FileKind.CSharp or FileKind.Xaml).Select(f => f.RelativePath).Order());
    }

    [Fact]
    public void Wpf_temporary_projects_are_not_tracked() =>
        Assert.Null(FileInventory.KindOf("src/App/App_x2fbmh1q_wpftmp.csproj"));

    [Fact]
    public async Task Folders_vanishing_mid_walk_are_skipped()
    {
        using var repo = TempRepo.Create(("src/A.cs", "class A { }"));
        var inv = new FileInventory(new RepoRoot(repo.Root), []);
        using var stop = new CancellationTokenSource();
        var churn = Task.Run(() =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                var top = Path.Combine(repo.Root, "churn", $"d{i % 20}");
                try
                {
                    Directory.CreateDirectory(Path.Combine(top, "sub"));
                    File.WriteAllText(Path.Combine(top, "sub", "x.cs"), "");
                    Directory.Delete(top, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        });
        try
        {
            for (var i = 0; i < 3000; i++) inv.Enumerate(); // must never throw
        }
        finally
        {
            stop.Cancel();
            await churn;
        }
    }

    [Fact]
    public void Stat_reports_current_size_and_null_for_missing_files()
    {
        using var repo = Sample();
        var inv = new FileInventory(new RepoRoot(repo.Root), []);
        Assert.Equal(11, inv.Stat("src/App/MainWindow.xaml.cs")!.Length);
        Assert.Null(inv.Stat("src/App/Nope.cs"));
    }
}
