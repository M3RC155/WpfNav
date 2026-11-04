namespace WpfNav.Tests.TestSupport;

public static class TestPaths
{
    /// <summary>The server exe built alongside the tests, in the same configuration (Debug or Release).</summary>
    public static string ServerExe { get; } = FindServerExe();

    static string FindServerExe()
    {
        var output = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var configuration = output.Parent!.Name;
        var dir = output;
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WpfNav.slnx"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException($"WpfNav.slnx not found above {AppContext.BaseDirectory}");
        return Path.Combine(dir.FullName, "src", "WpfNav.Server", "bin", configuration, "net10.0", "WpfNav.Server.exe");
    }
}
