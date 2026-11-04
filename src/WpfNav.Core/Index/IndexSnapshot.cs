using System.Globalization;
using WpfNav.Core.Model;

namespace WpfNav.Core.Index;

public sealed record IndexStatus(bool IsReady, int Parsed, int Total)
{
    public string Describe() => IsReady
        ? string.Create(CultureInfo.InvariantCulture, $"index: ready ({Total:N0} files)")
        : string.Create(CultureInfo.InvariantCulture,
            $"index {(Total == 0 ? 0 : (int)((long)Parsed * 100 / Total))}% ({Parsed:N0}/{Total:N0} files) — results may be incomplete");
}

/// <summary>An immutable view of the index; a new snapshot replaces it on every change.</summary>
public sealed class IndexSnapshot
{
    public IndexSnapshot(IReadOnlyDictionary<string, FileEntry> files, IndexStatus status)
    {
        Files = files;
        Status = status;
        Symbols = files.Values
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .SelectMany(f => f.Symbols)
            .Where(s => s.Kind != SymbolKind.Namespace)
            .ToList();
    }

    public static IndexSnapshot Empty { get; } =
        new(new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase), new IndexStatus(false, 0, 0));

    public IReadOnlyDictionary<string, FileEntry> Files { get; }

    /// <summary>Every symbol except namespaces, ordered by file path then source order.</summary>
    public IReadOnlyList<SymbolRecord> Symbols { get; }

    public IndexStatus Status { get; }
}
