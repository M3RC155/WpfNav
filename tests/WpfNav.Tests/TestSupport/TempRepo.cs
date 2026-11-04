namespace WpfNav.Tests.TestSupport;

/// <summary>A throwaway repository folder under %TEMP%\wpfnav-tests, deleted on dispose.</summary>
public sealed class TempRepo : IDisposable
{
    TempRepo(string root) => Root = root;

    public string Root { get; }

    public static TempRepo Create(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "wpfnav-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repo = new TempRepo(root);
        foreach (var (path, content) in files) repo.Write(path, content);
        return repo;
    }

    public string Write(string path, string content, Encoding? encoding = null)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, encoding ?? new UTF8Encoding(false));
        return full;
    }

    public void Delete(string path) => File.Delete(Full(path));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    string Full(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
}
