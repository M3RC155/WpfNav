namespace WpfNav.Tests.TestSupport;

public static class TestSymbols
{
    public static SymbolRecord Sym(string name, SymbolKind kind = SymbolKind.Class, string container = "Demo", string path = "src/X.cs",
        int line = 1, string? project = null, bool inTest = false, string[]? bases = null, bool partial = false) =>
        new(name, kind, container, $"{kind.Label()} {name}", path, line, line, line, "public", bases ?? [], [], partial)
        {
            Project = project,
            InTestProject = inTest,
        };
}
