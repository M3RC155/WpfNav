using System.Globalization;
using System.Text.RegularExpressions;
using WpfNav.Core.Index;
using WpfNav.Core.Inventory;
using WpfNav.Core.Model;
using WpfNav.Core.Output;
using WpfNav.Core.Parsing;

namespace WpfNav.Core.Navigation;

/// <summary>
/// How read_symbol pages source. Supplying <see cref="LineOffset"/> or <see cref="LineLimit"/> opts into explicit paging;
/// line offsets count from the declaration's first line (doc comments and attributes included), columns are UTF-16 positions
/// within the line at <see cref="LineOffset"/>.
/// </summary>
public sealed record ReadOptions(int? LineOffset = null, int? LineLimit = null, int ColumnOffset = 0, int? MaxTokens = null)
{
    public bool Paging => LineOffset is not null || LineLimit is not null;
}

/// <summary>Renders the read_symbol tool's text: the source of one member or type, read fresh from disk.</summary>
public static partial class SymbolReader
{
    public static Task<string> ReadAsync(Workspace ws, string symbol, string? path, CancellationToken ct) =>
        ReadAsync(ws, symbol, path, new ReadOptions(), ct);

    public static async Task<string> ReadAsync(Workspace ws, string symbol, string? path, ReadOptions options, CancellationToken ct)
    {
        var budget = Budget.Resolve(options.MaxTokens, Limits.ReadSymbol);
        Validate(options);
        var location = Location().Match(symbol);
        if (location.Success)
        {
            var digits = location.Groups[2].Value;
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var line) || line < 1)
                throw new WpfNavException(string.Create(CultureInfo.InvariantCulture,
                    $"'{Echo.Cut(symbol)}' names line {Echo.Cut(digits, 20)}; path:line needs a line number from 1 to {int.MaxValue:N0}."));
            return ReadAt(ws, location.Groups[1].Value, line, options, budget);
        }

        if (path is not null)
        {
            var file = SourceFile.Load(ws, path);
            var inFile = FindByName(file.Symbols, symbol);
            if (inFile.Count == 0) throw NotFound(symbol, $" in {Echo.Cut(file.Path)}", file.Symbols);
            return Render(ws, symbol, inFile.Select(c => (c, file)).ToList(), null, options, budget);
        }

        // Find candidates in the index, then re-parse their files so the source and line numbers are current.
        var snapshot = await ws.GetIndexAsync(ct);
        var indexed = FindByName(snapshot.Symbols, symbol);
        if (ws.Index.EnsureFresh(indexed.Select(s => s.Path)))
        {
            snapshot = ws.Index.Current;
            indexed = FindByName(snapshot.Symbols, symbol);
        }
        var candidates = new List<(SymbolRecord, SourceFile)>();
        foreach (var filePath in indexed.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            SourceFile fresh;
            try { fresh = SourceFile.Load(ws, filePath); }
            catch (WpfNavException) { continue; }
            var wanted = indexed.Where(s => s.Path.Equals(filePath, StringComparison.OrdinalIgnoreCase)).Select(s => (s.QualifiedName, s.Kind)).ToHashSet();
            candidates.AddRange(fresh.Symbols.Where(s => wanted.Contains((s.QualifiedName, s.Kind))).Select(s => (s, fresh)));
        }
        if (candidates.Count == 0) throw NotFound(symbol, "", snapshot.Symbols, snapshot.Status);
        return Render(ws, symbol, candidates, snapshot.Status, options, budget);
    }

    static void Validate(ReadOptions o)
    {
        if (o.LineOffset < 0) throw Error($"line_offset must be zero or more; got {o.LineOffset}.");
        if (o.LineLimit < 1) throw Error($"line_limit must be 1 or more; got {o.LineLimit}.");
        if (o.ColumnOffset < 0) throw Error($"column_offset must be zero or more; got {o.ColumnOffset}.");
        if (o.ColumnOffset > 0 && o.LineOffset is null) throw Error("column_offset needs line_offset; pass both from the same continuation note.");
    }

    /// <summary>Symbols whose name equals the last segment (case-sensitive first) and whose container ends with the rest.</summary>
    internal static List<SymbolRecord> FindByName(IEnumerable<SymbolRecord> symbols, string symbol)
    {
        var dot = symbol.LastIndexOf('.');
        var (suffix, name) = dot < 0 ? (null, symbol) : (symbol[..dot], symbol[(dot + 1)..]);
        var pool = symbols.Where(s => s.Kind != SymbolKind.Namespace && SymbolMatcher.ContainerMatches(s.Container, suffix)).ToList();
        var exact = pool.Where(s => s.Name.Equals(name, StringComparison.Ordinal)).ToList();
        return exact.Count > 0 ? exact : pool.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <param name="status">The index status behind a repo-wide lookup; when not ready, the message says results may be incomplete.</param>
    internal static WpfNavException NotFound(string symbol, string where, IEnumerable<SymbolRecord> pool, IndexStatus? status = null)
    {
        var dot = symbol.LastIndexOf('.');
        var closest = SymbolMatcher.Closest(pool, dot < 0 ? symbol : symbol[(dot + 1)..], 5);
        var suggestions = closest.Count == 0
            ? ""
            : " Closest: " + string.Join(", ", closest.Select(s => $"{s.Name} ({s.Kind.Label()}, {s.Path}:{s.StartLine})"));
        var incomplete = status is { IsReady: false } ? $" {status.Describe()}." : "";
        return new WpfNavException($"No symbol '{Echo.Cut(symbol)}'{where}.{suggestions}{incomplete}");
    }

    /// <summary>Renders one group of same-named candidates, or lists the candidates when they name different things.</summary>
    static string Render(Workspace ws, string symbolText, IReadOnlyList<(SymbolRecord Symbol, SourceFile File)> candidates,
        IndexStatus? status, ReadOptions options, int budget)
    {
        var w = new OutputWriter(budget, ws.Root.FullPath);
        if (status is { IsReady: false }) w.AppendRequired(status.Describe(), null, "the index status");
        var ordered = candidates.OrderBy(c => c.Symbol.Path, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Symbol.StartLine).ToList();
        // The same qualified name in two projects names two different types, so list them rather than merge them.
        if (ordered.Select(c => (ProjectOf(ws, c.Symbol.Path), c.Symbol.QualifiedName)).Distinct().Count() > 1)
        {
            Candidates(w, symbolText, ordered, options.Paging, also: null);
            return w.ToString();
        }

        // The XAML half of a partial view is named, not rendered: its "source" is the whole markup file.
        string? also = null;
        var xamlParts = ordered.Where(c => IsXamlPart(c.Symbol)).Select(c => c.Symbol).ToList();
        if (xamlParts.Count > 0 && xamlParts.Count < ordered.Count)
        {
            ordered = ordered.Where(c => !IsXamlPart(c.Symbol)).ToList();
            also = string.Join('\n', xamlParts.Select(p => $"also declared in: {p.Path}:{p.StartLine}"));
        }

        RenderGroup(w, symbolText, ordered, also, options);
        return w.ToString();
    }

    static bool IsXamlPart(SymbolRecord s) =>
        s.Kind == SymbolKind.Class && s.IsPartial && s.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);

    static string? ProjectOf(Workspace ws, string path) =>
        ws.Index.Current.Files.TryGetValue(path, out var entry) ? entry.Project : null;

    /// <param name="also">Lines naming parts of the declaration that are not rendered; kept room for in every path.</param>
    static void RenderGroup(OutputWriter w, string symbolText, List<(SymbolRecord Symbol, SourceFile File)> ordered, string? also, ReadOptions options)
    {
        if (options.Paging)
        {
            // One offset space per declaration: overloads and partial parts are chosen first, never concatenated.
            if (ordered.Count > 1) Candidates(w, symbolText, ordered, paging: true, also);
            else Page(w, ordered[0].Symbol, ordered[0].File, options.LineOffset ?? 0, options.LineLimit, options.ColumnOffset, also);
            return;
        }

        if (w.TryAppendBlock(GroupLines(ordered), also))
        {
            if (also is not null) w.AppendReserved(also);
        }
        else if (ordered.Count > 1)
        {
            Candidates(w, symbolText, ordered, paging: false, also);
        }
        else if (ordered[0].Symbol.Kind.IsType() && ordered[0].File.NodeOf(ordered[0].Symbol) is { } node)
        {
            TypeOutline(w, ordered[0].Symbol, ordered[0].File.Path, node, also);
        }
        else
        {
            Page(w, ordered[0].Symbol, ordered[0].File, 0, null, 0, also);
        }
    }

    /// <summary>Every candidate's header and source, a blank line between them; produced lazily so an oversized group stops early.</summary>
    static IEnumerable<string> GroupLines(List<(SymbolRecord Symbol, SourceFile File)> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0) yield return "";
            var (s, file) = ordered[i];
            yield return Header(s);
            for (var n = s.FullStartLine; n <= s.EndLine; n++) yield return $"{n}\t{file.Line(n)}";
        }
    }

    static string ReadAt(Workspace ws, string path, int line, ReadOptions options, int budget)
    {
        var file = SourceFile.Load(ws, path);
        var symbol = file.Symbols
            // The XAML class symbol spans the whole file; a location picks the element or resource around it, or nothing.
            .Where(s => s.Kind != SymbolKind.Namespace && !IsXamlPart(s) && s.FullStartLine <= line && line <= s.EndLine)
            .OrderBy(s => s.EndLine - s.FullStartLine)
            .ThenBy(s => s.Kind.IsType() ? 1 : 0)
            .FirstOrDefault()
            ?? throw new WpfNavException($"No symbol at {Echo.Cut(file.Path)}:{line}; use Read.");
        return Render(ws, $"{file.Path}:{line}", [(symbol, file)], null, options, budget);
    }

    static void Candidates(OutputWriter w, string symbolText, IEnumerable<(SymbolRecord Symbol, SourceFile File)> candidates, bool paging, string? also)
    {
        var list = candidates.Select(c => c.Symbol)
            .OrderBy(s => s.QualifiedName, StringComparer.Ordinal)
            .ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.StartLine)
            .ToList();
        var text = Echo.Cut(symbolText);
        string? Reserve(int shown) => Join(shown < list.Count
            ? $"… {list.Count - shown} more — pass a qualified name such as {Echo.Cut(list[0].QualifiedName)}, or path:line"
            : null, also);

        w.AppendRequired(paging
            ? $"{list.Count} declarations match \"{text}\"; line_offset paging reads one — retry with one of these as symbol, keeping line_offset:"
            : $"{list.Count} matches for \"{text}\" — pass a qualified name or path:line:", Reserve(0), "the candidate list header");
        var count = 0;
        foreach (var s in list)
        {
            var line = $"  {s.Kind.Label()} {s.QualifiedName}  {s.Path}:{s.StartLine}";
            if (count == 0) w.AppendProgress(line, Reserve(1), Limits.ReadSymbol, "the first candidate");
            else if (!w.TryAppend(line, Reserve(count + 1))) break;
            count++;
        }
        if (Reserve(count) is { } footer) w.AppendReserved(footer);
    }

    /// <summary>
    /// One page of a declaration's source from (lineOffset, column). Lines too long for any page are split into column
    /// fragments; other lines are never split. The footer names the lines shown and the next position, or the end.
    /// </summary>
    static void Page(OutputWriter w, SymbolRecord s, SourceFile file, int lineOffset, int? lineLimit, int column, string? also)
    {
        var total = s.EndLine - s.FullStartLine + 1;
        w.AppendRequired(Header(s), null, "the declaration header");
        if (also is not null) w.AppendRequired(also, null, "the other declarations of the type");
        if (lineOffset >= total)
        {
            w.AppendRequired(string.Create(CultureInfo.InvariantCulture,
                $"line_offset={lineOffset} is past the end of {Echo.Cut(s.QualifiedName)}: it has {total:N0} lines (line_offset 0-{total - 1}); nothing more to read"),
                null, "the end-of-declaration line");
            return;
        }

        var first = s.FullStartLine + lineOffset;
        if (column > 0) ValidateColumn(file.Line(first), first, column);
        var pageRoom = w.Room(); // room a page has for source and footer once the header is written
        var (n, col, touched) = (first, column, 0);
        while (true)
        {
            var text = file.Line(n);
            var whole = col == 0 ? $"{n}\t{text}" : Fragment(n, col, text.Length, text.AsSpan(col));
            var after = n == s.EndLine ? Done(s, first, n) : More(s, first, n, n + 1 - s.FullStartLine, 0);
            if (w.TryAppend(whole, after))
            {
                touched++;
                if (n == s.EndLine || touched == lineLimit)
                {
                    w.AppendReserved(after);
                    return;
                }
                (n, col) = (n + 1, 0);
                continue;
            }

            // The line does not fit here. A line that would fit an empty page waits for the next one; a longer line is
            // split, filling this page with a fragment that ends before a surrogate pair is cut.
            var wrote = n > first || col > column;
            var stop = More(s, first, n - 1, n - s.FullStartLine, 0);
            if (wrote && whole.Length + 1 + after.Length + 1 <= pageRoom)
            {
                w.AppendReserved(stop);
                return;
            }
            var upper = More(s, first, n, n - s.FullStartLine, text.Length);
            var room = w.Room(upper) - Fragment(n, col, text.Length, "").Length - 1;
            // Strictly shorter than the rest of the line, so the next position stays on this line.
            var end = col + Math.Clamp(room, 0, Math.Max(0, text.Length - 1 - col));
            if (end > col && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
            if (end <= col)
            {
                if (wrote)
                {
                    w.AppendReserved(stop);
                    return;
                }
                throw Budget.TooSmall(w.Limit, string.Create(CultureInfo.InvariantCulture,
                    $"one character of line {n} after the header of {Echo.Cut(s.QualifiedName)}"));
            }
            var footer = More(s, first, n, n - s.FullStartLine, end);
            w.AppendRequired(Fragment(n, col, end, text.AsSpan(col, end - col)), footer, "a source fragment");
            w.AppendReserved(footer);
            return;
        }
    }

    static void ValidateColumn(string text, int line, int column)
    {
        if (column >= text.Length && text.Length == 0)
            throw Error($"column_offset={column} is outside line {line}, which is empty; use column_offset=0.");
        if (column >= text.Length)
            throw Error($"column_offset={column} is outside line {line}, which has {text.Length} columns; use 0-{text.Length - 1}.");
        if (char.IsLowSurrogate(text[column]) && char.IsHighSurrogate(text[column - 1]))
            throw Error($"column_offset={column} splits a surrogate pair on line {line}; use {column - 1} or {column + 1}.");
    }

    static string Fragment(int line, int from, int to, ReadOnlySpan<char> text) =>
        string.Create(CultureInfo.InvariantCulture, $"{line} [cols {from}..{to})\t{text}");

    static string Shown(SymbolRecord s, int from, int to) =>
        string.Create(CultureInfo.InvariantCulture, $"{(from == to ? $"line {from}" : $"lines {from}-{to}")} of {s.FullStartLine}-{s.EndLine} shown");

    static string More(SymbolRecord s, int from, int to, int lineOffset, int column) =>
        string.Create(CultureInfo.InvariantCulture, $"… {Shown(s, from, to)}; repeat the same target with line_offset={lineOffset}, column_offset={column}");

    static string Done(SymbolRecord s, int from, int to) => $"({Shown(s, from, to)}; end of declaration)";

    static void TypeOutline(OutputWriter w, SymbolRecord s, string path, OutlineNode node, string? also)
    {
        w.AppendRequired(Header(s), null, "the declaration header");
        var lines = (s.EndLine - s.FullStartLine + 1).ToString("N0", CultureInfo.InvariantCulture);
        var example = node.Children.Select(c => c.Symbol).FirstOrDefault(c => c.Kind.IsMember());
        var rows = OutlineBuilder.CSharpRows(node.Children, 2, members: true);
        string? Reserve(int shown) => Join(shown < rows.Count
            ? $"… {rows.Count - shown} more members — outline {Echo.Cut(path)} for the rest, or page the type's source with line_offset=0"
            : null, also);
        w.AppendRequired($"type is {lines} lines — showing its outline; read a member directly"
            + (example is null ? "" : $", e.g. read_symbol {s.Name}.{example.Name}") + ", or page the type's source with line_offset=0",
            Reserve(0), "the type summary");
        var count = rows.TakeWhile((r, i) => w.TryAppend(r.Text, Reserve(i + 1))).Count();
        if (Reserve(count) is { } footer) w.AppendReserved(footer);
    }

    static string Header(SymbolRecord s) =>
        s.FullStartLine == s.EndLine
            ? $"{s.Path}:{s.EndLine}  {s.QualifiedName}"
            : $"{s.Path}:{s.FullStartLine}-{s.EndLine}  {s.QualifiedName}";

    static string? Join(string? first, string? second) =>
        first is null ? second : second is null ? first : first + "\n" + second;

    static WpfNavException Error(string message) => new(message);

    [GeneratedRegex(@"^(.+):(\d+)$")]
    private static partial Regex Location();
}

/// <summary>One source file parsed fresh from disk, with its lines and symbols.</summary>
internal sealed class SourceFile
{
    readonly string[] _lines;
    readonly Dictionary<SymbolRecord, OutlineNode> _nodes = new(ReferenceEqualityComparer.Instance);

    SourceFile(string path, string text, IReadOnlyList<SymbolRecord> symbols, IEnumerable<OutlineNode> roots)
    {
        Path = path;
        _lines = text.Split('\n');
        Symbols = symbols;
        Index(roots);
    }

    public string Path { get; }

    public IReadOnlyList<SymbolRecord> Symbols { get; }

    public static SourceFile Load(Workspace ws, string path)
    {
        var relative = ws.Root.ToRelative(ws.Root.Resolve(path));
        var kind = FileInventory.KindOf(relative);
        if (kind is not (FileKind.CSharp or FileKind.Xaml))
            throw new WpfNavException($"Unsupported file type: {Echo.Cut(relative)} (read_symbol takes .cs and .xaml files)");
        var text = SourceParser.ReadText(ws.Root, relative);
        if (kind == FileKind.Xaml) return new SourceFile(relative, text, XamlOutliner.Parse(text, relative).Symbols, []);
        var outline = CSharpOutliner.Parse(text, relative);
        return new SourceFile(relative, text, outline.AllSymbols().ToList(), outline.Roots);
    }

    /// <summary>The decoded line without its line break; past the end of the file, empty.</summary>
    public string Line(int number) => number - 1 < _lines.Length ? _lines[number - 1].TrimEnd('\r') : "";

    public OutlineNode? NodeOf(SymbolRecord symbol) => _nodes.GetValueOrDefault(symbol);

    void Index(IEnumerable<OutlineNode> nodes)
    {
        foreach (var node in nodes)
        {
            _nodes[node.Symbol] = node;
            Index(node.Children);
        }
    }
}
