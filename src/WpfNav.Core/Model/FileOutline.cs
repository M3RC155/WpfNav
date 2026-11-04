namespace WpfNav.Core.Model;

public sealed record OutlineNode(SymbolRecord Symbol, IReadOnlyList<OutlineNode> Children);

public sealed record FileOutline(string Path, int LineCount, IReadOnlyList<OutlineNode> Roots)
{
    /// <summary>Every symbol, parents before children, in source order.</summary>
    public IEnumerable<SymbolRecord> AllSymbols() => Flatten(Roots);

    static IEnumerable<SymbolRecord> Flatten(IEnumerable<OutlineNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node.Symbol;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }
}
