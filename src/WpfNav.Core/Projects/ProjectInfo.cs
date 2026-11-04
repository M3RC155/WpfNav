namespace WpfNav.Core.Projects;

public enum ProjectKind { WpfApp, WpfLibrary, App, Test, Library }

public static class ProjectKinds
{
    public static string Label(this ProjectKind kind) => kind switch
    {
        ProjectKind.WpfApp => "WPF app",
        ProjectKind.WpfLibrary => "WPF library",
        ProjectKind.App => "app",
        ProjectKind.Test => "test",
        _ => "library",
    };
}

public sealed record PackageRef(string Name, string? Version);

/// <param name="Directory">Repo-relative folder holding the project file ("" at the root).</param>
/// <param name="Approximate">True when a value was inherited from Directory.Build.props.</param>
/// <param name="Error">"not parsed: …" when the project file could not be read.</param>
public sealed record ProjectInfo(
    string Name,
    string Path,
    string Directory,
    ProjectKind Kind,
    IReadOnlyList<string> TargetFrameworks,
    bool Approximate,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<PackageRef> Packages,
    string? Error);

public sealed record SolutionInfo(string Name, string Path, IReadOnlyList<string> ProjectPaths);
