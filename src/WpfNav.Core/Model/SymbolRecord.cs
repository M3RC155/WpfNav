namespace WpfNav.Core.Model;

/// <summary>One declaration: what it is, where it is, and how it reads in one line.</summary>
/// <param name="StartLine">Line of the first token after any attribute lists.</param>
/// <param name="FullStartLine">Line where the doc comment or first attribute starts; equals StartLine when there are none.</param>
/// <param name="Bases">Simple names of base types (generic arguments removed).</param>
/// <param name="Attributes">Simple attribute names without the "Attribute" suffix.</param>
public sealed record SymbolRecord(
    string Name,
    SymbolKind Kind,
    string Container,
    string Signature,
    string Path,
    int StartLine,
    int EndLine,
    int FullStartLine,
    string Accessibility,
    IReadOnlyList<string> Bases,
    IReadOnlyList<string> Attributes,
    bool IsPartial)
{
    public string? Project { get; init; }

    public bool InTestProject { get; init; }

    public string QualifiedName => Container.Length == 0 ? Name : $"{Container}.{Name}";
}
