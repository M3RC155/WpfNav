using WpfNav.Core.Inventory;
using WpfNav.Core.Model;

namespace WpfNav.Core.Parsing;

/// <summary>Reads a source file from disk (decoding byte-order marks) and outlines it.</summary>
public static class SourceParser
{
    public static FileOutline ParseCSharp(RepoRoot root, string relativePath) =>
        CSharpOutliner.Parse(ReadText(root, relativePath), relativePath);

    /// <param name="withCodeBehind">Also parse "{path}.cs", when it exists, so event handlers can be recognised.</param>
    /// <param name="uncappedTree">Keep every kept child of the reduced tree; the default caps each element at 20 children.</param>
    public static XamlOutline ParseXaml(RepoRoot root, string relativePath, bool withCodeBehind, bool uncappedTree = false)
    {
        var text = ReadText(root, relativePath);
        var context = XamlContext.Empty;
        var codeBehind = relativePath + ".cs";
        if (withCodeBehind && File.Exists(root.Resolve(codeBehind)))
        {
            var methods = ParseCSharp(root, codeBehind).AllSymbols()
                .Where(s => s.Kind == SymbolKind.Method).Select(s => s.Name).ToHashSet();
            context = new XamlContext(codeBehind, methods);
        }
        return XamlOutliner.Parse(text, relativePath, context, uncappedTree);
    }

    public static string ReadText(RepoRoot root, string relativePath)
    {
        try
        {
            return File.ReadAllText(root.Resolve(relativePath));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new WpfNavException($"File not found: {Output.Echo.Cut(relativePath)}");
        }
    }
}
