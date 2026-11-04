using static WpfNav.Tests.Navigation.SymbolReaderTests;

namespace WpfNav.Tests.Navigation;

public class SourceFragmentTests
{
    static Workspace Ws(TempRepo repo) => new(new RepoRoot(repo.Root), []);

    /// <summary>A 20,000+ character line (about 6,000 estimated tokens) with surrogate pairs sprinkled through it.</summary>
    static readonly string HugeLine = "        var s = \"" + string.Concat(Enumerable.Range(0, 2000).Select(i => i % 7 == 0 ? $"😀{i:0000}" : $"word{i:0000}x")) + "\";";

    static string Code(string line, string newline = "\n") =>
        string.Join(newline, "class Data", "{", "    string Make()", "    {", line, "        return s;", "    }", "}", "");

    [Theory, InlineData(256), InlineData(1000), InlineData(4000)]
    public async Task One_huge_line_reconstructs_from_fragments_without_gaps_or_repeats(int budget)
    {
        Assert.True(OutputWriter.EstimateTokens(HugeLine) > Limits.ReadSymbol);
        using var repo = TempRepo.Create(("src/Data.cs", Code(HugeLine)));
        var pages = await AllPages(Ws(repo), "Make", "src/Data.cs", budget);
        Assert.True(pages.Count >= 2);
        Assert.Equal(Code(HugeLine).Split('\n').Select((text, i) => (i + 1, text)).Where(x => x.Item1 is >= 3 and <= 7), Source(pages));
        Assert.Contains(pages, p => p.Contains("\n5 [cols 0..", StringComparison.Ordinal));
        Assert.DoesNotContain(pages, p => p.Contains("\n5\t", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_ordinary_read_of_a_huge_line_starts_with_a_fragment_and_a_column_continuation()
    {
        using var repo = TempRepo.Create(("src/Data.cs", Code(HugeLine)));
        var text = await SymbolReader.ReadAsync(Ws(repo), "Make", "src/Data.cs", default);
        Assert.True(OutputWriter.EstimateTokens(text) <= Limits.ReadSymbol);
        var m = Regex.Match(text, @"\n5 \[cols 0\.\.(\d+)\)\t.*\n… lines 3-5 of 3-7 shown; repeat the same target with line_offset=2, column_offset=(\d+)\n$");
        Assert.True(m.Success, text[^300..]);
        Assert.Equal(m.Groups[1].Value, m.Groups[2].Value);
    }

    [Theory, InlineData(256), InlineData(4000)]
    public async Task Fragments_never_split_surrogate_pairs(int budget)
    {
        var emoji = "        var e = \"" + string.Concat(Enumerable.Repeat("😀", 9000)) + "\";";
        using var repo = TempRepo.Create(("src/Data.cs", Code(emoji)));
        var pages = await AllPages(Ws(repo), "Make", "src/Data.cs", budget);
        var fragments = pages.SelectMany(p => p.Split('\n')).Select(l => Regex.Match(l, @"^5 \[cols (\d+)\.\.(\d+)\)\t(.*)$")).Where(m => m.Success).ToList();
        Assert.True(fragments.Count > 1);
        foreach (var f in fragments)
        {
            var text = f.Groups[3].Value;
            Assert.False(char.IsLowSurrogate(text[0]), $"fragment at {f.Groups[1].Value} starts inside a pair");
            Assert.False(char.IsHighSurrogate(text[^1]), $"fragment ending at {f.Groups[2].Value} ends inside a pair");
        }
        Assert.Equal(emoji, Source(pages).Single(l => l.Line == 5).Text);
    }

    [Fact]
    public async Task Column_offsets_inside_a_pair_or_past_the_line_are_rejected()
    {
        const string line = "        var e = \"😀\";";
        using var repo = TempRepo.Create(("src/Data.cs", Code(line)));
        var pair = line.IndexOf("😀", StringComparison.Ordinal);
        Assert.Equal($"column_offset={pair + 1} splits a surrogate pair on line 5; use {pair} or {pair + 2}.", (await Assert.ThrowsAsync<WpfNavException>(() =>
            SymbolReader.ReadAsync(Ws(repo), "Make", "src/Data.cs", new ReadOptions(LineOffset: 2, ColumnOffset: pair + 1), default))).Message);
        Assert.Equal($"column_offset={line.Length} is outside line 5, which has {line.Length} columns; use 0-{line.Length - 1}.", (await Assert.ThrowsAsync<WpfNavException>(() =>
            SymbolReader.ReadAsync(Ws(repo), "Make", "src/Data.cs", new ReadOptions(LineOffset: 2, ColumnOffset: line.Length), default))).Message);
        Assert.Contains($"\n5 [cols {pair}..{line.Length})\t😀\";\n", await SymbolReader.ReadAsync(Ws(repo), "Make", "src/Data.cs", new ReadOptions(LineOffset: 2, ColumnOffset: pair), default));
    }

    [Fact]
    public async Task Crlf_long_line_reconstructs_without_carriage_returns()
    {
        using var repo = TempRepo.Create(("src/Data.cs", Code(HugeLine, "\r\n")));
        var pages = await AllPages(Ws(repo), "Make", "src/Data.cs", Budget.Minimum);
        Assert.DoesNotContain(pages, p => p.Contains('\r'));
        Assert.Equal(HugeLine, Source(pages).Single(l => l.Line == 5).Text);
    }
}
