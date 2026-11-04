using WpfNav.Core.Inventory;
using WpfNav.Core.Model;

namespace WpfNav.Core.Index;

public sealed record XamlRootInfo(string RootElement, string? XClass, IReadOnlyDictionary<string, string> RootAttributes);

/// <summary>What the index keeps per file: its symbols and enough metadata to notice when it changes. No source text.</summary>
public sealed record FileEntry(
    string Path,
    FileKind Kind,
    long Length,
    DateTime LastWriteUtc,
    int LineCount,
    string? Project,
    bool InTestProject,
    IReadOnlyList<SymbolRecord> Symbols,
    XamlRootInfo? Xaml);
