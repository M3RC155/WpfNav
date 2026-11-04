namespace WpfNav.Core.Model;

/// <summary>A resource-dictionary entry. Key is null for implicit styles and templates.</summary>
public sealed record XamlResource(string? Key, string ElementType, string? Detail, int StartLine, int EndLine);

/// <summary>An element kept in the reduced tree, labelled with its name, UI text and interesting attributes.</summary>
public sealed record XamlNode(string Label, int StartLine, int EndLine, IReadOnlyList<XamlNode> Children, int HiddenChildren);

/// <summary>What the outliner knows about the code-behind file, used to recognise event handlers.</summary>
public sealed record XamlContext(string? CodeBehindPath, IReadOnlySet<string> CodeBehindMethods)
{
    public static XamlContext Empty { get; } = new(null, new HashSet<string>());
}

/// <param name="Tree">The root element's kept children; the root itself is described by the header fields.</param>
/// <param name="HiddenTopLevel">Kept children of the root beyond the 20 shown.</param>
public sealed record XamlOutline(
    string Path,
    int LineCount,
    string RootElement,
    string? XClass,
    string? CodeBehindPath,
    string? DataContext,
    IReadOnlyDictionary<string, string> RootAttributes,
    IReadOnlyList<XamlResource> Resources,
    IReadOnlyList<string> MergedDictionaries,
    IReadOnlyList<XamlNode> Tree,
    int HiddenTopLevel,
    IReadOnlyList<SymbolRecord> Symbols,
    string? Error);
