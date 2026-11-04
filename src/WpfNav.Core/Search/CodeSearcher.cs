using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using WpfNav.Core.Index;
using WpfNav.Core.Inventory;
using WpfNav.Core.Model;
using WpfNav.Core.Output;
using WpfNav.Core.Projects;

namespace WpfNav.Core.Search;

public sealed record SearchRequest(
    string Pattern,
    bool Literal = false,
    bool WholeWord = false,
    bool CaseSensitive = true,
    string FileKind = "all",
    string? Project = null,
    string? Path = null,
    int Limit = 15,
    int Offset = 0)
{
    /// <summary>Estimated-token budget for the whole result; null selects <see cref="Limits.SearchCode"/>.</summary>
    public int? MaxTokens { get; init; }
}

/// <summary>Line-based regex search over .cs/.xaml files, with hits grouped by file and containing member.</summary>
public static class CodeSearcher
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(15);
    const int MaxSample = 120;

    public static async Task<string> SearchAsync(Workspace ws, SearchRequest request, CancellationToken ct)
    {
        var budget = Budget.Resolve(request.MaxTokens, Limits.SearchCode);
        var regex = BuildRegex(request);
        FileKind[] kinds = request.FileKind.ToLowerInvariant() switch
        {
            "cs" => [FileKind.CSharp],
            "xaml" => [FileKind.Xaml],
            "all" => [FileKind.CSharp, FileKind.Xaml],
            _ => throw new WpfNavException($"Unknown file_kind '{Echo.Cut(request.FileKind)}'. Use cs, xaml or all."),
        };
        var projects = await ws.GetProjectsAsync(ct);
        ProjectInfo? project = null;
        if (request.Project is { } name && (project = projects.FindByName(name)) is null)
        {
            var closest = SymbolMatcher.ClosestNames(projects.Projects.Select(p => p.Name), name, 5);
            throw new WpfNavException($"Unknown project '{Echo.Cut(name)}'." + (closest.Count == 0 ? "" : $" Closest: {string.Join(", ", closest)}"));
        }
        var prefix = request.Path is { Length: > 0 } p ? ws.Root.ToRelative(ws.Root.Resolve(p)) : null;
        if (prefix == ".") prefix = null;

        // A second pass runs only when a file with hits changed since it was indexed, so groups use current line ranges.
        for (var attempt = 0; ; attempt++)
        {
            var snapshot = attempt == 0 ? await ws.GetIndexAsync(ct) : ws.Index.Current;
            var files = snapshot.Files.Values
                .Where(f => kinds.Contains(f.Kind)
                    && (project is null || string.Equals(f.Project, project.Name, StringComparison.OrdinalIgnoreCase))
                    && (prefix is null || f.Path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || f.Path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var (results, timedOut, stopped) = await ScanAsync(ws, files, regex, ct);
            if (attempt == 0 && ws.Index.EnsureFresh(results.Select(r => r.File.Path))) continue;
            return Render(ws, snapshot, request, budget, results, timedOut, stopped);
        }
    }

    static Regex BuildRegex(SearchRequest request)
    {
        var pattern = request.Literal ? Regex.Escape(request.Pattern) : request.Pattern;
        if (request.WholeWord) pattern = $@"\b(?:{pattern})\b";
        var options = RegexOptions.CultureInvariant | (request.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new Regex(pattern, options, MatchTimeout);
        }
        catch (ArgumentException e)
        {
            throw new WpfNavException($"Invalid regex: {Echo.Cut(e.Message, 2 * Echo.MaxChars)} Pass literal=true to search for the text as-is.");
        }
    }

    static async Task<(List<FileHits> Results, int TimedOut, bool Stopped)> ScanAsync(
        Workspace ws, List<FileEntry> files, Regex regex, CancellationToken ct)
    {
        var results = new ConcurrentBag<FileHits>();
        var timedOut = 0;
        var stopped = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(SearchTimeout);
        try
        {
            await Parallel.ForEachAsync(files, new ParallelOptions { CancellationToken = deadline.Token, MaxDegreeOfParallelism = Environment.ProcessorCount },
                (file, token) =>
                {
                    string text;
                    try { text = File.ReadAllText(ws.Root.Resolve(file.Path)); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ValueTask.CompletedTask; }

                    var lines = text.Split('\n');
                    var hits = new List<(int Line, string Text)>();
                    try
                    {
                        for (var i = 0; i < lines.Length; i++)
                        {
                            token.ThrowIfCancellationRequested();
                            var line = lines[i].TrimEnd('\r');
                            if (regex.IsMatch(line)) hits.Add((i + 1, line));
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        Interlocked.Increment(ref timedOut);
                        return ValueTask.CompletedTask;
                    }
                    if (hits.Count > 0) results.Add(new FileHits(file, hits));
                    return ValueTask.CompletedTask;
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopped = true;
        }
        var ordered = results
            .OrderBy(r => r.File.InTestProject ? 1 : 0)
            .ThenByDescending(r => r.Hits.Count)
            .ThenBy(r => r.File.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (ordered, timedOut, stopped);
    }

    static string Render(Workspace ws, IndexSnapshot snapshot, SearchRequest request, int budget, List<FileHits> results, int timedOut, bool stopped)
    {
        var groups = results.Select(r => (r.File, Groups: GroupHits(r))).ToList();
        var totalGroups = groups.Sum(g => g.Groups.Count);
        var offset = Math.Max(0, request.Offset);
        var limit = Math.Max(1, request.Limit);
        string? Footer(int shown) => totalGroups - offset - shown is var remaining and > 0 && offset < totalGroups
            ? OutputWriter.MoreNote(remaining, offset + shown)
            : null;

        var w = new OutputWriter(budget, ws.Root.FullPath);
        if (!snapshot.Status.IsReady) w.AppendRequired(snapshot.Status.Describe(), Footer(0), "the index status");
        w.AppendRequired($"\"{Echo.Cut(request.Pattern)}\": {Plural(results.Sum(r => r.Hits.Count), "hit")} in {Plural(results.Count, "file")} ({Plural(totalGroups, "member")})"
            + (timedOut > 0 ? $" (regex timed out in {Plural(timedOut, "file")})" : ""), Footer(0), "the search summary");
        if (stopped) w.AppendRequired($"search stopped after {SearchTimeout.TotalSeconds:0} s — results are partial", Footer(0), "the partial-results warning");

        var index = 0;
        var shown = 0;
        foreach (var (file, fileGroups) in groups)
        {
            var headerWritten = false;
            foreach (var group in fileGroups)
            {
                if (index++ < offset) continue;
                if (shown >= limit) goto done;
                // A file's header and its first group go together, so a page never ends on a bare header.
                var block = (headerWritten ? "" : $"{file.Path} ({group.FileHits})\n") + group.Line;
                if (shown == 0) w.AppendProgress(block, Footer(1), Limits.SearchCode, "the first match group");
                else if (!w.TryAppend(block, Footer(shown + 1))) goto done;
                headerWritten = true;
                shown++;
            }
        }
        done:
        if (Footer(shown) is { } footer) w.AppendReserved(footer);
        return w.ToString();
    }

    /// <summary>Groups a file's hits by the innermost symbol containing each line, in order of first hit.</summary>
    static List<HitGroup> GroupHits(FileHits result)
    {
        var symbols = result.File.Symbols.Where(s => s.Kind != SymbolKind.Namespace).ToList();
        return result.Hits
            .GroupBy(h => symbols.Where(s => s.FullStartLine <= h.Line && h.Line <= s.EndLine)
                .OrderBy(s => s.EndLine - s.FullStartLine)
                .ThenBy(s => s.Kind.IsType() ? 1 : 0) // equal spans (one-line declarations): the member is the inner one
                .FirstOrDefault())
            .Select(g =>
            {
                var hits = g.ToList();
                var label = g.Key is null ? "(file)" : $"{Label(g.Key)} {Range(g.Key)}";
                var count = hits.Count == 1 ? "" : $"{hits.Count} hits — ";
                return new HitGroup($"  {label}: {count}L{hits[0].Line}: {Sample(hits[0].Text)}", result.Hits.Count, hits[0].Line);
            })
            .OrderBy(g => g.FirstLine)
            .ToList();
    }

    static string Label(SymbolRecord s) => s.Kind switch
    {
        SymbolKind.XamlElement or SymbolKind.XamlResource or SymbolKind.TopLevelStatements => s.Signature,
        _ when s.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) => s.Signature,
        _ when s.Kind.IsMember() => $"{s.Container.Split('.')[^1]}.{s.Name}",
        _ => s.Name,
    };

    static string Range(SymbolRecord s) => s.EndLine > s.StartLine ? $"[{s.StartLine}-{s.EndLine}]" : $"[{s.StartLine}]";

    static string Sample(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= MaxSample ? trimmed : trimmed[..(MaxSample - 1)] + "…";
    }

    static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    sealed record FileHits(FileEntry File, List<(int Line, string Text)> Hits);

    sealed record HitGroup(string Line, int FileHits, int FirstLine);
}
