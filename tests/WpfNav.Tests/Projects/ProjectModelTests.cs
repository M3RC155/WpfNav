namespace WpfNav.Tests.Projects;

public class ProjectModelTests
{
    const string Sdk = "<Project Sdk=\"Microsoft.NET.Sdk\">";

    static TempRepo Repo() => TempRepo.Create(
        ("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net8.0-windows</TargetFramework></PropertyGroup></Project>"),
        ("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"CommunityToolkit.Mvvm\" Version=\"8.2.2\" /></ItemGroup></Project>"),
        ("src/Shell/Shell.csproj", Sdk + "<PropertyGroup><OutputType>WinExe</OutputType><UseWPF>true</UseWPF></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Core\\Core.csproj\" /><ProjectReference Include=\"..\\Orders\\Orders.csproj\" /><PackageReference Include=\"CommunityToolkit.Mvvm\" /></ItemGroup></Project>"),
        ("src/Orders/Orders.csproj", Sdk + "<PropertyGroup><UseWPF>true</UseWPF></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Core\\Core.csproj\" /></ItemGroup></Project>"),
        ("src/Core/Core.csproj", Sdk + "<PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"),
        ("tests/Core.Tests/Core.Tests.csproj", Sdk + "<ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.3\" /><ProjectReference Include=\"..\\..\\src\\Core\\Core.csproj\" /></ItemGroup></Project>"),
        ("legacy/Old/Old.csproj", """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
                <ProjectTypeGuids>{60dc8134-eba5-43b8-bcc9-bb4bc16c2548};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}</ProjectTypeGuids>
              </PropertyGroup>
            </Project>
            """),
        ("legacy/Old/packages.config", "<packages><package id=\"Newtonsoft.Json\" version=\"13.0.1\" /></packages>"),
        ("broken/Bad.csproj", "<Project"),
        ("All.slnx", "<Solution><Project Path=\"src/Shell/Shell.csproj\" /><Folder Name=\"/lib/\"><Project Path=\"src/Core/Core.csproj\" /></Folder></Solution>"),
        ("All.sln", Sln("Shell", @"src\Shell\Shell.csproj")),
        ("Legacy.sln", Sln("Old", @"legacy\Old\Old.csproj")));

    static string Sln(string name, string path) => $$"""
        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "{{name}}", "{{path}}", "{11111111-1111-1111-1111-111111111111}"
        EndProject
        Global
        EndGlobal
        """;

    static async Task<ProjectModel> Load(TempRepo repo)
    {
        var root = new RepoRoot(repo.Root);
        return await ProjectModel.LoadAsync(root, new FileInventory(root, []).Enumerate(), default);
    }

    [Fact]
    public async Task Reads_kinds_frameworks_packages_and_references()
    {
        using var repo = Repo();
        var m = await Load(repo);
        var shell = m.FindByName("shell")!;
        Assert.Equal((ProjectKind.WpfApp, true), (shell.Kind, shell.Approximate));
        Assert.Equal(new[] { "net8.0-windows" }, shell.TargetFrameworks);
        Assert.Equal(new[] { new PackageRef("CommunityToolkit.Mvvm", "8.2.2") }, shell.Packages);
        Assert.Equal(new[] { "src/Core/Core.csproj", "src/Orders/Orders.csproj" }, shell.ProjectReferences);
        Assert.Equal(ProjectKind.WpfLibrary, m.FindByName("Orders")!.Kind);
        Assert.Equal((ProjectKind.Library, false), (m.FindByName("Core")!.Kind, m.FindByName("Core")!.Approximate));
        Assert.Equal(ProjectKind.Test, m.FindByName("Core.Tests")!.Kind);
        var old = m.FindByName("Old")!;
        Assert.Equal((ProjectKind.WpfApp, "net472"), (old.Kind, old.TargetFrameworks.Single()));
        Assert.Equal(new[] { new PackageRef("Newtonsoft.Json", "13.0.1") }, old.Packages);
        Assert.StartsWith("not parsed:", m.FindByName("Bad")!.Error);
    }

    [Fact]
    public async Task Orders_dependents_first_and_maps_files_to_projects()
    {
        using var repo = Repo();
        var m = await Load(repo);
        Assert.Equal(new[] { "Old", "Shell", "Orders", "Core.Tests", "Core", "Bad" }, m.Projects.Select(p => p.Name));
        Assert.Equal("Orders", m.ProjectForFile("src/Orders/Views/OrderView.xaml")!.Name);
        Assert.Null(m.ProjectForFile("tools/x.cs"));
        Assert.Equal(new[] { "Shell", "Orders", "Core.Tests" }, m.ReferencedBy(m.FindByName("Core")!).Select(p => p.Name));
    }

    [Fact]
    public async Task Lists_solutions_preferring_slnx()
    {
        using var repo = Repo();
        var m = await Load(repo);
        Assert.Equal(new[] { "All.slnx", "Legacy.sln" }, m.Solutions.Select(s => s.Path));
        Assert.Equal(new[] { "src/Shell/Shell.csproj", "src/Core/Core.csproj" }, m.Solutions[0].ProjectPaths);
        Assert.Equal(new[] { "legacy/Old/Old.csproj" }, m.Solutions[1].ProjectPaths);
    }
}
