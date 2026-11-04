using WpfNav.Core.Index;
using WpfNav.Core.Model;
using WpfNav.Core.Output;

namespace WpfNav.Core.Navigation;

/// <summary>Renders the find_symbol tool's text: ranked declarations, one line each.</summary>
public static class SymbolFinder
{
    const int MaxPartsShown = 5, MaxClosest = 5;

    public static Task<string> FindAsync(Workspace ws, SymbolQuery query, int limit, int offset, CancellationToken ct) =>
        FindAsync(ws, query, limit, offset, maxTokens: null, ct);

    /// <param name="maxTokens">Estimated-token budget for the whole result; null selects <see cref="Limits.FindSymbol"/>.</param>
    public static async Task<string> FindAsync(Workspace ws, SymbolQuery query, int limit, int offset, int? maxTokens, CancellationToken ct)
    {
        var budget = Budget.Resolve(maxTokens, Limits.FindSymbol);
        var projects = await ws.GetProjectsAsync(ct);
        if (query.Project is { } project && projects.FindByName(project) is null)
        {
            var closest = SymbolMatcher.ClosestNames(projects.Projects.Select(p => p.Name), project, 5);
            throw new WpfNavException($"Unknown project '{Echo.Cut(project)}'." + (closest.Count == 0 ? "" : $" Closest: {string.Join(", ", closest)}"));
        }
        var path = query.Path is { Length: > 0 } p ? ws.Root.ToRelative(ws.Root.Resolve(p)) : null;
        var snapshot = await ws.GetIndexAsync(ct);
        var effective = query with { Path = path == "." ? null : path };
        var matches = SymbolMatcher.Match(snapshot.Symbols, effective);
        var shownPaths = matches.Skip(Math.Max(0, offset)).Take(Math.Max(1, limit)).SelectMany(m => m.OtherParts.Prepend(m.Symbol)).Select(s => s.Path);
        if (ws.Index.EnsureFresh(shownPaths))
        {
            snapshot = ws.Index.Current;
            matches = SymbolMatcher.Match(snapshot.Symbols, effective);
        }

        var w = new OutputWriter(budget, ws.Root.FullPath);
        var text = Echo.Cut(query.Text);
        if (matches.Count == 0)
        {
            if (!snapshot.Status.IsReady) w.AppendRequired(snapshot.Status.Describe(), null, "the index status");
            var dot = query.Text.LastIndexOf('.');
            var name = (dot < 0 ? query.Text : query.Text[(dot + 1)..]).Trim('*', '?');
            var closest = SymbolMatcher.Closest(snapshot.Symbols, name, MaxClosest)
                .Select(s => $"{s.Name} ({s.Kind.Label()}, {s.Path}:{s.StartLine})").ToList();
            // Drop suggestions from the end until the line fits; the bare "0 matches" line is required.
            for (var n = closest.Count; ; n--)
            {
                var line = $"0 matches for \"{text}\"." + (n == 0 ? "" : " Closest: " + string.Join(", ", closest.Take(n)));
                if (n == 0) w.AppendRequired(line, null, "the no-match line");
                else if (!w.TryAppend(line)) continue;
                return w.ToString();
            }
        }

        offset = Math.Max(0, offset);
        var page = matches.Skip(offset).Take(Math.Max(1, limit)).ToList();
        string? Footer(int shown) => matches.Count - offset - shown is var remaining and > 0
            ? OutputWriter.MoreNote(remaining, offset + shown)
            : null;

        if (!snapshot.Status.IsReady) w.AppendRequired(snapshot.Status.Describe(), Footer(0), "the index status");
        w.AppendRequired(matches.Count == 1 ? $"1 match for \"{text}\"" : $"{matches.Count} matches for \"{text}\"", Footer(0), "the match count");
        var count = 0;
        foreach (var m in page)
        {
            if (count == 0) w.AppendProgress(Line(m), Footer(1), Limits.FindSymbol, "the first match");
            else if (!w.TryAppend(Line(m), Footer(count + 1))) break;
            count++;
        }
        if (Footer(count) is { } footer) w.AppendReserved(footer);
        return w.ToString();
    }

    static string Line(SymbolMatch m)
    {
        var s = m.Symbol;
        var line = s.Container.Length == 0 ? $"{s.Signature}  {s.Path}:{s.StartLine}" : $"{s.Signature}  [{s.Container}]  {s.Path}:{s.StartLine}";
        if (m.OtherParts.Count == 0) return line;
        var parts = string.Join(", ", m.OtherParts.Take(MaxPartsShown).Select(o => $"{o.Path}:{o.StartLine}"))
            + (m.OtherParts.Count > MaxPartsShown ? $", … {m.OtherParts.Count - MaxPartsShown} more" : "");
        return $"{line}  (+{m.OtherParts.Count} part{(m.OtherParts.Count == 1 ? "" : "s")}: {parts})";
    }
}
