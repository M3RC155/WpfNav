using System.Globalization;
using WpfNav.Core.Inventory;
using WpfNav.Core.Model;
using WpfNav.Core.Output;
using WpfNav.Core.Parsing;

namespace WpfNav.Core.Navigation;

/// <summary>One rendered outline row. Entries are what offsets count; other rows are context. Parent is a row index or -1.</summary>
internal sealed record OutlineRow(string Text, bool IsEntry, int Parent);

/// <summary>
/// Renders the outline tool's text for files and folders, one page at a time. A page starts at (offset, entry_offset):
/// files to skip in the resolved file sequence, then entries to skip in the first remaining file.
/// </summary>
public static class OutlineBuilder
{
    const int EnumMembersShown = 8, MaxGroupedRow = 200;
    const string Continued = " (continued)";

    /// <param name="offset">Resolved files to skip.</param>
    /// <param name="entryOffset">Entries to skip in the first remaining file.</param>
    /// <param name="maxTokens">Estimated-token budget for the page; null selects <see cref="Limits.Outline"/>.</param>
    public static string Build(Workspace ws, IReadOnlyList<string> paths, string? level = null, int offset = 0, int entryOffset = 0, int? maxTokens = null)
    {
        var budget = Budget.Resolve(maxTokens, Limits.Outline);
        if (offset < 0) throw new WpfNavException(string.Create(CultureInfo.InvariantCulture, $"offset must be zero or more; got {offset}."));
        if (entryOffset < 0) throw new WpfNavException(string.Create(CultureInfo.InvariantCulture, $"entry_offset must be zero or more; got {entryOffset}."));
        bool? members = level switch
        {
            null => null,
            "types" => false,
            "members" => true,
            _ => throw new WpfNavException($"Unknown level '{Echo.Cut(level)}'. Use types or members."),
        };

        var (files, empty) = Resolve(ws, paths);
        var w = new OutputWriter(budget, ws.Root.FullPath);
        if (empty.Count > 0)
            w.AppendRequired($"No .cs or .xaml files under {Echo.Cut(empty[0])}" + (empty.Count > 1 ? $" and {Plural(empty.Count - 1, "more path")}" : ""),
                null, "the empty-folder note");
        if (files.Count == 0) return w.ToString();

        var page = new Page(w, files.Count);
        for (var fi = offset; fi < files.Count; fi++)
        {
            var start = fi == offset ? entryOffset : 0;
            var file = Render(ws, files[fi].Path, members ?? !files[fi].FromFolder);
            if (start > 0 && start >= file.Entries.Count) continue; // this file is already done
            if (!page.Write(file, fi, start)) return w.ToString();
        }
        if (!page.Wrote)
            w.AppendRequired(string.Create(CultureInfo.InvariantCulture,
                $"no more outline entries at offset={offset}, entry_offset={entryOffset} ({Plural(files.Count, "file")} in total)"), null, "the end-of-results line");
        return w.ToString();
    }

    /// <summary>
    /// The deterministic file sequence: requested order, each folder sorted case-insensitively (ordinal tie-break),
    /// a file requested twice kept at its first position. Also returns the folders that held no .cs or .xaml files.
    /// </summary>
    static (List<(string Path, bool FromFolder)> Files, List<string> Empty) Resolve(Workspace ws, IReadOnlyList<string> paths)
    {
        var files = new List<(string Path, bool FromFolder)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var empty = new List<string>();
        foreach (var path in paths)
        {
            var full = ws.Root.Resolve(path);
            var relative = ws.Root.ToRelative(full);
            if (Directory.Exists(full))
            {
                var found = ws.Inventory.Enumerate(relative)
                    .Where(f => f.Kind is FileKind.CSharp or FileKind.Xaml)
                    .Select(f => f.RelativePath)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f, StringComparer.Ordinal)
                    .ToList();
                if (found.Count == 0) empty.Add(relative);
                files.AddRange(found.Where(seen.Add).Select(f => (f, true)));
            }
            else if (!File.Exists(full)) throw new WpfNavException($"File not found: {Echo.Cut(relative)}");
            else if (FileInventory.KindOf(relative) is not (FileKind.CSharp or FileKind.Xaml))
                throw new WpfNavException($"Unsupported file type: {Echo.Cut(relative)} (outline takes .cs and .xaml files or folders)");
            else if (seen.Add(relative)) files.Add((relative, false));
        }
        return (files, empty);
    }

    /// <summary>One file's outline: header lines repeated on each of its pages, then rows, with the row index of each entry.</summary>
    sealed class FileRows
    {
        public FileRows(string path, IReadOnlyList<string> header, List<OutlineRow> rows)
        {
            Path = path;
            Header = header;
            Rows = rows;
            Entries = rows.Select((r, i) => (r, i)).Where(x => x.r.IsEntry).Select(x => x.i).ToList();
        }

        public string Path { get; }
        public IReadOnlyList<string> Header { get; }
        public List<OutlineRow> Rows { get; }
        public List<int> Entries { get; }

        /// <summary>The context rows written just before entry <paramref name="e"/> when the page flows into it.</summary>
        public IEnumerable<string> Lead(int e)
        {
            var from = e == 0 ? 0 : Entries[e - 1] + 1;
            for (var i = from; i < Entries[e]; i++) yield return Rows[i].Text;
        }

        /// <summary>The context for a page that starts at entry e: ancestors shown on earlier pages are marked as continued.</summary>
        public IEnumerable<string> ContextAt(int e)
        {
            if (e == 0) return Lead(0);
            var leadFrom = Entries[e - 1] + 1;
            var ancestors = new List<string>();
            for (var p = Rows[Entries[e]].Parent; p >= 0; p = Rows[p].Parent)
                if (p < leadFrom) ancestors.Add(Rows[p].Text + Continued);
            ancestors.Reverse();
            return ancestors.Concat(Lead(e));
        }

        /// <summary>Context rows after the last entry (an empty trailing namespace, say), shown with it.</summary>
        public IEnumerable<string> Trail() => Rows.Skip(Entries.Count == 0 ? 0 : Entries[^1] + 1).Select(r => r.Text);
    }

    /// <summary>Writes files into one page, reserving before each unit the exact footer needed if the page ends after it.</summary>
    sealed class Page(OutputWriter w, int fileCount)
    {
        public bool Wrote { get; private set; }

        /// <summary>Writes entries of the file from <paramref name="start"/>; false once the page is full (its footer is then written).</summary>
        public bool Write(FileRows file, int fi, int start)
        {
            var n = file.Entries.Count;
            if (n == 0)
            {
                // A file without entries (XAML at the types level, a file with no declarations) is its header.
                var unit = file.Header.Concat(file.Trail()).ToList();
                return Put(unit, Footer(fi, 0, 0), FilesFooter(fi)!, file.Path);
            }
            for (var e = start; e < n; e++)
            {
                var unit = new List<string>();
                if (e == start)
                {
                    unit.AddRange(file.Header);
                    unit.AddRange(file.ContextAt(e));
                }
                else
                {
                    unit.AddRange(file.Lead(e));
                }
                unit.Add(file.Rows[file.Entries[e]].Text);
                if (e == n - 1) unit.AddRange(file.Trail());
                // When this unit does not fit, the page ends at its position; the previous unit reserved that footer.
                var here = e == 0 ? FilesFooter(fi)! : EntriesFooter(fi, e, n);
                if (!Put(unit, Footer(fi, e + 1, n), here, file.Path)) return false;
            }
            return true;
        }

        /// <summary>Writes the unit with room kept for <paramref name="reserve"/>; otherwise ends the page with <paramref name="here"/>.</summary>
        bool Put(List<string> unit, string? reserve, string here, string path)
        {
            if (!Wrote)
            {
                w.AppendProgress(string.Join('\n', unit), reserve, Limits.Outline, $"the next outline entry of {Echo.Cut(path)}");
                Wrote = true;
                return true;
            }
            if (w.TryAppendBlock(unit, reserve)) return true;
            w.AppendReserved(here);
            return false;
        }

        /// <summary>The footer for a page ending at entry e of file fi (which has n entries), or null when nothing remains.</summary>
        string? Footer(int fi, int e, int n) => e < n ? EntriesFooter(fi, e, n) : FilesFooter(fi + 1);

        string EntriesFooter(int fi, int e, int n)
        {
            var laterFiles = fileCount - fi - 1;
            return string.Create(CultureInfo.InvariantCulture,
                $"… {Plural(n - e, "more entry", "more entries")} in this file{(laterFiles > 0 ? $" and {Plural(laterFiles, "more file")}" : "")}; repeat the same paths/level with offset={fi}, entry_offset={e}");
        }

        string? FilesFooter(int fi) => fi < fileCount
            ? string.Create(CultureInfo.InvariantCulture, $"… {Plural(fileCount - fi, "more file")}; repeat the same paths/level with offset={fi}, entry_offset=0")
            : null;
    }

    static FileRows Render(Workspace ws, string path, bool members)
    {
        if (FileInventory.KindOf(path) == FileKind.Xaml)
            return XamlRows(SourceParser.ParseXaml(ws.Root, path, withCodeBehind: true, uncappedTree: true), members);
        var outline = SourceParser.ParseCSharp(ws.Root, path);
        return new FileRows(path, [$"{path} ({Count(outline.LineCount)} lines)"], CSharpRows(outline.Roots, 2, members));
    }

    /// <summary>C# rows: namespaces (context, members level only), types, members, enums with a summary, grouped private fields.</summary>
    internal static List<OutlineRow> CSharpRows(IEnumerable<OutlineNode> nodes, int indent, bool members)
    {
        var rows = new List<OutlineRow>();
        Add(nodes.ToList(), indent, -1);
        return rows;

        void Add(IReadOnlyList<OutlineNode> level, int at, int parent)
        {
            var groupedFields = members
                ? level.Select(n => n.Symbol).Where(s => s.Kind == SymbolKind.Field && s.Attributes.Count == 0 && !s.Accessibility.Contains("public")).ToList()
                : [];
            foreach (var node in level)
            {
                var s = node.Symbol;
                var pad = new string(' ', at);
                if (s.Kind == SymbolKind.Namespace)
                {
                    if (members)
                    {
                        rows.Add(new(new string(' ', Math.Max(0, at - 2)) + s.Signature, false, parent));
                        Add(node.Children, at, rows.Count - 1);
                    }
                    else
                    {
                        Add(node.Children, at, parent);
                    }
                }
                else if (groupedFields.Contains(s))
                {
                    if (s == groupedFields[0])
                        rows.AddRange(Chunks($"{pad}fields: ", groupedFields.Select(f => f.Name)).Select(c => new OutlineRow(c, true, parent)));
                }
                else if (s.Kind == SymbolKind.Enum)
                {
                    rows.Add(new($"{pad}{Attributes(s)}{s.Signature}{(members ? EnumMembers(node) : "")} {Range(s)}", true, parent));
                }
                else if (s.Kind.IsType())
                {
                    rows.Add(new($"{pad}{Attributes(s)}{s.Signature} {Range(s)}", true, parent));
                    Add(node.Children, at + 2, rows.Count - 1);
                }
                else if (members)
                {
                    rows.Add(new($"{pad}{Attributes(s)}{s.Signature} {Range(s)}", true, parent));
                }
            }
        }
    }

    static FileRows XamlRows(XamlOutline x, bool members)
    {
        var header = new List<string> { $"{x.Path} ({Count(x.LineCount)} lines)  {x.RootElement}" + (x.XClass is null ? "" : $"  x:Class={x.XClass}") };
        if (x.Error is not null) header.Add($"  {Echo.Cut(x.Error, 2 * Echo.MaxChars)}");
        var rows = new List<OutlineRow>();
        if (members)
        {
            if (x.CodeBehindPath is not null) rows.Add(new($"  code-behind: {x.CodeBehindPath}", false, -1));
            if (x.DataContext is not null) rows.Add(new($"  DataContext: {x.DataContext}", false, -1));
            if (x.Resources.Count > 0)
            {
                rows.Add(new("  resources:", false, -1));
                var label = rows.Count - 1;
                rows.AddRange(x.Resources.Select(r => new OutlineRow(
                    $"    {r.Key ?? "(implicit)"}  {r.ElementType}{(r.Detail is null ? "" : " " + r.Detail)} {Range(r.StartLine, r.EndLine)}", true, label)));
            }
            rows.AddRange(Chunks("  merged: ", x.MergedDictionaries).Select(c => new OutlineRow(c, true, -1)));
            AddTree(x.Tree, x.HiddenTopLevel, 2, -1);
        }
        return new FileRows(x.Path, header, rows);

        void AddTree(IReadOnlyList<XamlNode> nodes, int hidden, int at, int parent)
        {
            foreach (var n in nodes)
            {
                rows.Add(new($"{new string(' ', at)}{n.Label} {Range(n.StartLine, n.EndLine)}", true, parent));
                AddTree(n.Children, n.HiddenChildren, at + 2, rows.Count - 1);
            }
            if (hidden > 0) rows.Add(new($"{new string(' ', at)}… {hidden} more", true, parent));
        }
    }

    /// <summary>"prefix a, b, c" rows of at most 200 characters (one item per row at least), so long groups page like other entries.</summary>
    static IEnumerable<string> Chunks(string prefix, IEnumerable<string> items)
    {
        var row = new System.Text.StringBuilder();
        foreach (var item in items)
        {
            if (row.Length > 0 && row.Length + 2 + item.Length > MaxGroupedRow)
            {
                yield return row.ToString();
                row.Clear();
            }
            row.Append(row.Length == 0 ? prefix : ", ").Append(item);
        }
        if (row.Length > 0) yield return row.ToString();
    }

    static string EnumMembers(OutlineNode node)
    {
        var names = node.Children.Select(c => c.Symbol.Name).ToList();
        var shown = string.Join(", ", names.Take(EnumMembersShown));
        return names.Count > EnumMembersShown ? $" {{ {shown}, … ({names.Count}) }}" : $" {{ {shown} }}";
    }

    static string Attributes(SymbolRecord s) => string.Concat(s.Attributes.Select(a => $"[{a}]")) + (s.Attributes.Count > 0 ? " " : "");

    static string Range(SymbolRecord s) => Range(s.StartLine, s.EndLine);

    static string Range(int start, int end) => end > start ? $"[{start}-{end}]" : $"[{start}]";

    static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    static string Plural(int n, string word) => Plural(n, word, word + "s");

    static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n.ToString(CultureInfo.InvariantCulture)} {many}";
}
