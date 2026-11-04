using WpfNav.Tests.Parsing;

namespace WpfNav.Tests.Navigation;

public class SymbolReaderTests
{
    const string Svc = """
        namespace Demo;
        public class OrderService
        {
            /// <summary>Saves.</summary>
            [Obsolete]
            public void Save(int id)
            {
                Console.WriteLine(id);
            }
            public void Save(string sku) { }
            public int Count => 0;
        }
        """;

    static Workspace Ws(TempRepo repo) => new(new RepoRoot(repo.Root), []);

    [Fact]
    public async Task Reads_one_member_with_line_numbers()
    {
        using var repo = TempRepo.Create(("src/Svc.cs", Svc));
        Assert.Equal(TestText.Lf($"root: {repo.Root}\nsrc/Svc.cs:11  Demo.OrderService.Count\n11\t    public int Count => 0;\n"),
            await SymbolReader.ReadAsync(Ws(repo), "OrderService.Count", "src/Svc.cs", default));
    }

    [Fact]
    public async Task Overloads_are_shown_together_with_doc_comments_and_attributes()
    {
        using var repo = TempRepo.Create(("src/Svc.cs", Svc));
        var text = await SymbolReader.ReadAsync(Ws(repo), "Save", "src/Svc.cs", default);
        Assert.Contains("src/Svc.cs:4-9  Demo.OrderService.Save\n4\t    /// <summary>Saves.</summary>\n5\t    [Obsolete]\n6\t    public void Save(int id)\n", text);
        Assert.Contains("src/Svc.cs:10  Demo.OrderService.Save\n10\t    public void Save(string sku) { }\n", text);
    }

    [Fact]
    public async Task Path_and_line_picks_the_innermost_member()
    {
        using var repo = TempRepo.Create(("src/Svc.cs", Svc));
        Assert.Contains("src/Svc.cs:4-9  Demo.OrderService.Save\n", await SymbolReader.ReadAsync(Ws(repo), "src/Svc.cs:8", null, default));
    }

    [Fact]
    public async Task Same_name_in_two_types_lists_candidates()
    {
        using var repo = TempRepo.Create(("src/Two.cs", "namespace Demo;\nclass A\n{\n    void Run() { }\n}\nclass B\n{\n    void Run() { }\n}\n"));
        Assert.Equal(TestText.Lf($"root: {repo.Root}\n2 matches for \"Run\" — pass a qualified name or path:line:\n  method Demo.A.Run  src/Two.cs:4\n  method Demo.B.Run  src/Two.cs:8\n"),
            await SymbolReader.ReadAsync(Ws(repo), "Run", "src/Two.cs", default));
    }

    [Fact]
    public async Task A_member_over_the_limit_returns_its_first_page_and_a_continuation()
    {
        using var repo = TempRepo.Create(("src/Big.cs", BigSource()));
        var text = await SymbolReader.ReadAsync(Ws(repo), "Long", "src/Big.cs", default);
        Assert.True(OutputWriter.EstimateTokens(text) <= Limits.ReadSymbol);
        Assert.StartsWith(TestText.Lf($"root: {repo.Root}\nsrc/Big.cs:3-2005  Big.Long\n3\t    void Long()\n4\t    {{\n5\t        Console.WriteLine(0);\n"), text);
        var m = Regex.Match(text, @"\n… lines 3-(\d+) of 3-2005 shown; repeat the same target with line_offset=(\d+), column_offset=0\n$");
        Assert.True(m.Success, text[^200..]);
        Assert.Equal(int.Parse(m.Groups[1].Value) - 2, int.Parse(m.Groups[2].Value));
    }

    static string BigSource() =>
        $"class Big\n{{\n    void Long()\n    {{\n{string.Concat(Enumerable.Range(0, 2000).Select(i => $"        Console.WriteLine({i});\n"))}    }}\n}}\n";

    static string WideSource() =>
        $"class Wide\n{{\n{string.Concat(Enumerable.Range(0, 300).Select(i => $"    public int M{i:000}(int x)\n    {{\n        return x + {i};\n    }}\n"))}}}\n";

    [Fact]
    public async Task A_type_over_the_limit_returns_its_outline_with_member_and_paging_hints()
    {
        using var repo = TempRepo.Create(("src/Wide.cs", WideSource()));
        var text = await SymbolReader.ReadAsync(Ws(repo), "Wide", "src/Wide.cs", default);
        Assert.Contains("src/Wide.cs:1-1203  Wide\ntype is 1,203 lines — showing its outline; read a member directly, e.g. read_symbol Wide.M000, or page the type's source with line_offset=0\n", text);
        Assert.Contains("  public int M000(int x) [3-6]\n", text);
        Assert.EndsWith("  public int M299(int x) [1199-1202]\n", text);
        Assert.True(OutputWriter.EstimateTokens(text) <= Limits.ReadSymbol);
        var small = await SymbolReader.ReadAsync(Ws(repo), "Wide", "src/Wide.cs", new ReadOptions(MaxTokens: 1000), default);
        Assert.True(OutputWriter.EstimateTokens(small) <= 1000);
        Assert.Matches(@"\n… \d+ more members — outline src/Wide.cs for the rest, or page the type's source with line_offset=0\n$", small);
    }

    /// <summary>Follows continuation footers from line_offset=0 until a page has none, checking every page's budget and progress.</summary>
    internal static async Task<List<string>> AllPages(Workspace ws, string symbol, string? path, int? budget, int? lineLimit = null)
    {
        var pages = new List<string>();
        var position = (Line: 0, Column: 0);
        for (var guard = 0; guard < 5000; guard++)
        {
            var page = await SymbolReader.ReadAsync(ws, symbol, path, new ReadOptions(position.Line, lineLimit, position.Column, budget), default);
            Assert.True(OutputWriter.EstimateTokens(page) <= (budget ?? Limits.ReadSymbol), $"page {guard}: {OutputWriter.EstimateTokens(page)} tokens");
            pages.Add(page);
            var m = Regex.Match(page, @"repeat the same target with line_offset=(\d+), column_offset=(\d+)\n$");
            if (!m.Success) return pages;
            var next = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            Assert.True(next.CompareTo(position) > 0, $"page {guard} did not advance from {position}");
            position = next;
        }
        throw new Xunit.Sdk.XunitException("source did not end within 5000 pages");
    }

    /// <summary>Source lines reassembled from pages: "n\ttext" lines and "n [cols a..b)\ttext" fragments, each piece exactly once.</summary>
    internal static List<(int Line, string Text)> Source(IEnumerable<string> pages)
    {
        var lines = new SortedDictionary<int, StringBuilder>();
        var complete = new HashSet<int>();
        foreach (var row in pages.SelectMany(p => p.Split('\n')))
        {
            if (Regex.Match(row, @"^(\d+)\t(.*)$", RegexOptions.Singleline) is { Success: true } full)
            {
                var n = int.Parse(full.Groups[1].Value);
                Assert.True(lines.TryAdd(n, new StringBuilder(full.Groups[2].Value)), $"line {n} repeated");
                complete.Add(n);
            }
            else if (Regex.Match(row, @"^(\d+) \[cols (\d+)\.\.(\d+)\)\t(.*)$", RegexOptions.Singleline) is { Success: true } part)
            {
                var n = int.Parse(part.Groups[1].Value);
                var (from, to, text) = (int.Parse(part.Groups[2].Value), int.Parse(part.Groups[3].Value), part.Groups[4].Value);
                Assert.False(complete.Contains(n), $"fragment after the whole of line {n}");
                var sb = lines.TryGetValue(n, out var existing) ? existing : lines[n] = new StringBuilder();
                Assert.Equal(sb.Length, from);
                Assert.Equal(to - from, text.Length);
                sb.Append(text);
            }
        }
        return lines.Select(kv => (kv.Key, kv.Value.ToString())).ToList();
    }

    [Theory, InlineData(256), InlineData(1000), InlineData(4000)]
    public async Task A_long_member_reconstructs_across_pages_at_every_budget(int budget)
    {
        using var repo = TempRepo.Create(("src/Big.cs", BigSource()));
        var pages = await AllPages(Ws(repo), "Long", "src/Big.cs", budget);
        Assert.True(pages.Count > 1);
        Assert.All(pages, p => Assert.Contains("\nsrc/Big.cs:3-2005  Big.Long\n", p));
        var expected = BigSource().Split('\n').Select((text, i) => (i + 1, text)).Where(x => x.Item1 is >= 3 and <= 2005);
        Assert.Equal(expected, Source(pages));
        Assert.EndsWith("of 3-2005 shown; end of declaration)\n", pages[^1]);
    }

    [Fact]
    public async Task A_type_pages_its_whole_source_from_line_offset_zero()
    {
        using var repo = TempRepo.Create(("src/Wide.cs", WideSource()));
        var pages = await AllPages(Ws(repo), "Wide", "src/Wide.cs", null);
        Assert.Equal(WideSource().Split('\n').Take(1203).Select((text, i) => (i + 1, text)), Source(pages));
    }

    const string Doc = "namespace Demo;\npublic class Doc\n{\n    /// <summary>Runs.</summary>\n    [Obsolete]\n    public void Run()\n    {\n\n        Work();\n    }\n}\n";

    [Fact]
    public async Task Paging_counts_from_the_doc_comment_keeps_blank_lines_and_honors_line_limit()
    {
        using var repo = TempRepo.Create(("src/Doc.cs", Doc));
        var ws = Ws(repo);
        Assert.Equal(TestText.Lf($"root: {repo.Root}\nsrc/Doc.cs:4-10  Demo.Doc.Run\n4\t    /// <summary>Runs.</summary>\n5\t    [Obsolete]\n6\t    public void Run()\n7\t    {{\n8\t\n9\t        Work();\n10\t    }}\n(lines 4-10 of 4-10 shown; end of declaration)\n"),
            await SymbolReader.ReadAsync(ws, "Run", "src/Doc.cs", new ReadOptions(LineOffset: 0), default));
        Assert.Equal(TestText.Lf($"root: {repo.Root}\nsrc/Doc.cs:4-10  Demo.Doc.Run\n4\t    /// <summary>Runs.</summary>\n5\t    [Obsolete]\n6\t    public void Run()\n… lines 4-6 of 4-10 shown; repeat the same target with line_offset=3, column_offset=0\n"),
            await SymbolReader.ReadAsync(ws, "Run", "src/Doc.cs", new ReadOptions(LineLimit: 3), default));
        Assert.Equal(TestText.Lf($"root: {repo.Root}\nsrc/Doc.cs:4-10  Demo.Doc.Run\n8\t\n9\t        Work();\n10\t    }}\n(lines 8-10 of 4-10 shown; end of declaration)\n"),
            await SymbolReader.ReadAsync(ws, "Run", "src/Doc.cs", new ReadOptions(LineOffset: 4, LineLimit: 3), default));
    }

    [Fact]
    public async Task The_exact_end_finishes_and_past_the_end_says_so_without_a_continuation()
    {
        using var repo = TempRepo.Create(("src/Doc.cs", Doc));
        var ws = Ws(repo);
        Assert.Equal(TestText.Lf($"root: {repo.Root}\nsrc/Doc.cs:4-10  Demo.Doc.Run\n10\t    }}\n(line 10 of 4-10 shown; end of declaration)\n"),
            await SymbolReader.ReadAsync(ws, "Run", "src/Doc.cs", new ReadOptions(LineOffset: 6), default));
        Assert.Equal(TestText.Lf($"root: {repo.Root}\nsrc/Doc.cs:4-10  Demo.Doc.Run\nline_offset=7 is past the end of Demo.Doc.Run: it has 7 lines (line_offset 0-6); nothing more to read\n"),
            await SymbolReader.ReadAsync(ws, "Run", "src/Doc.cs", new ReadOptions(LineOffset: 7, ColumnOffset: 3), default));
    }

    [Fact]
    public async Task Explicit_paging_of_overloads_asks_for_one_and_path_line_stays_anchored()
    {
        using var repo = TempRepo.Create(("src/Svc.cs", Svc));
        var ws = Ws(repo);
        Assert.Equal(TestText.Lf($"root: {repo.Root}\n2 declarations match \"Save\"; line_offset paging reads one — retry with one of these as symbol, keeping line_offset:\n  method Demo.OrderService.Save  src/Svc.cs:6\n  method Demo.OrderService.Save  src/Svc.cs:10\n"),
            await SymbolReader.ReadAsync(ws, "Save", "src/Svc.cs", new ReadOptions(LineOffset: 0), default));
        Assert.Equal(TestText.Lf($"root: {repo.Root}\nsrc/Svc.cs:4-9  Demo.OrderService.Save\n5\t    [Obsolete]\n6\t    public void Save(int id)\n… lines 5-6 of 4-9 shown; repeat the same target with line_offset=3, column_offset=0\n"),
            await SymbolReader.ReadAsync(ws, "src/Svc.cs:6", null, new ReadOptions(LineOffset: 1, LineLimit: 2), default));
        Assert.Contains("\n8\t        Console.WriteLine(id);\n", await SymbolReader.ReadAsync(ws, "src/Svc.cs:6", null, new ReadOptions(LineOffset: 3), default));
    }

    [Fact]
    public async Task Explicit_paging_of_partial_parts_lists_their_selectors()
    {
        using var repo = TempRepo.Create(("src/P.cs", "namespace Demo;\npublic partial class P\n{\n    void A() { }\n}\n"), ("src/P.More.cs", "namespace Demo;\npublic partial class P\n{\n    void B() { }\n}\n"));
        using var ws = TestWorkspace.Started(repo);
        var text = await SymbolReader.ReadAsync(ws, "P", null, new ReadOptions(LineOffset: 0), default);
        Assert.Contains("2 declarations match \"P\"; line_offset paging reads one", text);
        Assert.Contains("\n  class Demo.P  src/P.cs:2\n", text);
        Assert.Contains("\n  class Demo.P  src/P.More.cs:2\n", text);
        Assert.Contains("\n4\t    void B() { }\n", await SymbolReader.ReadAsync(ws, "src/P.More.cs:2", null, new ReadOptions(LineOffset: 0), default));
    }

    [Theory, InlineData(256), InlineData(null)]
    public async Task A_xaml_subtree_pages_within_its_element(int? budget)
    {
        var rows = string.Concat(Enumerable.Range(0, 300).Select(i => $"    <TextBlock Text=\"Row {i:000} of the long grid\" />\n"));
        var view = $"<UserControl xmlns=\"p\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n  <StackPanel x:Name=\"Rows\">\n{rows}  </StackPanel>\n</UserControl>\n";
        using var repo = TempRepo.Create(("src/V.xaml", view));
        var pages = await AllPages(Ws(repo), "Rows", "src/V.xaml", budget);
        Assert.Equal(view.Split('\n').Select((text, i) => (i + 1, text)).Where(x => x.Item1 is >= 2 and <= 303), Source(pages));
    }

    [Theory]
    [InlineData(-1, null, 0, "line_offset must be zero or more; got -1.")]
    [InlineData(null, 0, 0, "line_limit must be 1 or more; got 0.")]
    [InlineData(0, null, -1, "column_offset must be zero or more; got -1.")]
    [InlineData(null, null, 4, "column_offset needs line_offset; pass both from the same continuation note.")]
    [InlineData(2, null, 99, "column_offset=99 is outside line 6, which has 21 columns; use 0-20.")]
    public async Task Invalid_offsets_are_actionable_errors(int? line, int? limit, int column, string message)
    {
        using var repo = TempRepo.Create(("src/Doc.cs", Doc));
        Assert.Equal(message, (await Assert.ThrowsAsync<WpfNavException>(() =>
            SymbolReader.ReadAsync(Ws(repo), "Run", "src/Doc.cs", new ReadOptions(line, limit, column), default))).Message);
    }

    [Theory]
    [InlineData("src/Doc.cs:0")]
    [InlineData("src/Doc.cs:99999999999")]
    public async Task Locator_lines_must_be_positive_representable_numbers(string symbol)
    {
        using var repo = TempRepo.Create(("src/Doc.cs", Doc));
        var ex = await Assert.ThrowsAsync<WpfNavException>(() => SymbolReader.ReadAsync(Ws(repo), symbol, null, default));
        Assert.Equal($"'{symbol}' names line {symbol.Split(':')[1]}; path:line needs a line number from 1 to 2,147,483,647.", ex.Message);
    }

    [Fact]
    public async Task Each_page_rereads_the_file_so_edits_between_pages_show_up()
    {
        using var repo = TempRepo.Create(("src/Doc.cs", Doc));
        var ws = Ws(repo);
        Assert.Contains("\n6\t    public void Run()\n", await SymbolReader.ReadAsync(ws, "Run", "src/Doc.cs", new ReadOptions(LineLimit: 3), default));
        repo.Write("src/Doc.cs", Doc.Replace("Work();", "WorkHarder();"));
        Assert.Contains("\n9\t        WorkHarder();\n", await SymbolReader.ReadAsync(ws, "Run", "src/Doc.cs", new ReadOptions(LineOffset: 3), default));
    }

    [Fact]
    public async Task Budgets_outside_the_range_are_rejected()
    {
        using var repo = TempRepo.Create(("src/Doc.cs", Doc));
        Assert.Equal("max_tokens must be between 256 and 4000; got 4001. Omit it for the default (4000).", (await Assert.ThrowsAsync<WpfNavException>(() =>
            SymbolReader.ReadAsync(Ws(repo), "Run", "src/Doc.cs", new ReadOptions(MaxTokens: 4001), default))).Message);
    }

    [Fact]
    public async Task Xaml_names_read_their_element()
    {
        using var repo = TempRepo.Create(("src/V.xaml", XamlOutlinerTests.View));
        Assert.Contains("src/V.xaml:24-25  Contoso.Orders.Views.OrderEditView.LinesGrid\n24\t",
            await SymbolReader.ReadAsync(Ws(repo), "LinesGrid", "src/V.xaml", default));
    }

    [Fact]
    public async Task Unknown_names_suggest_close_matches()
    {
        using var repo = TempRepo.Create(("src/Svc.cs", Svc));
        Assert.Equal("No symbol 'Sav' in src/Svc.cs. Closest: Save (method, src/Svc.cs:6)",
            (await Assert.ThrowsAsync<WpfNavException>(() => SymbolReader.ReadAsync(Ws(repo), "Sav", "src/Svc.cs", default))).Message);
    }

    [Fact]
    public async Task Same_partial_type_in_two_projects_is_two_candidates()
    {
        using var repo = TempRepo.Create(
            ("a/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"), ("a/MainWindow.xaml.cs", "namespace Demo;\npublic partial class MainWindow { }\n"),
            ("b/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"), ("b/MainWindow.xaml.cs", "namespace Demo;\npublic partial class MainWindow { }\n"));
        using var ws = TestWorkspace.Started(repo);
        Assert.StartsWith(TestText.Lf($"root: {repo.Root}\n2 matches for \"MainWindow\" — pass a qualified name or path:line:\n"),
            await SymbolReader.ReadAsync(ws, "MainWindow", null, default));
    }

    [Fact]
    public async Task Xaml_location_outside_named_elements_points_to_Read()
    {
        using var repo = TempRepo.Create(("src/V.xaml", XamlOutlinerTests.View));
        Assert.Equal("No symbol at src/V.xaml:19; use Read.",
            (await Assert.ThrowsAsync<WpfNavException>(() => SymbolReader.ReadAsync(Ws(repo), "src/V.xaml:19", null, default))).Message);
    }

    [Fact]
    public async Task A_long_candidate_list_ends_with_a_hint()
    {
        var types = string.Concat(Enumerable.Range(0, 400).Select(i => $"class C{i:000} {{ void Execute() {{ }} }}\n"));
        using var repo = TempRepo.Create(("src/Many.cs", types));
        var text = await SymbolReader.ReadAsync(Ws(repo), "Execute", "src/Many.cs", default);
        Assert.True(OutputWriter.EstimateTokens(text) <= Limits.ReadSymbol);
        Assert.Matches(@"… \d+ more — pass a qualified name such as C000\.Execute, or path:line\n$", text);
    }

    [Fact]
    public async Task Name_lookup_says_when_the_index_is_incomplete()
    {
        using var repo = TempRepo.Create(("src/A.cs", "class A { }"));
        using var idle = new Workspace(new RepoRoot(repo.Root), [], indexWait: TimeSpan.Zero, watch: false);
        var ex = await Assert.ThrowsAsync<WpfNavException>(() => SymbolReader.ReadAsync(idle, "A", null, default));
        Assert.EndsWith("— results may be incomplete.", ex.Message);
    }

    [Fact]
    public async Task Name_lookup_without_path_uses_the_index()
    {
        using var repo = SampleRepo.Create();
        using var ws = TestWorkspace.Started(repo);
        Assert.Contains("src/Core/Services/OrderService.cs:4  Core.Services.OrderService.Create\n",
            await SymbolReader.ReadAsync(ws, "OrderService.Create", null, default));
        Assert.Equal(TestText.Lf($"root: {repo.Root}\n2 matches for \"MainViewModel\" — pass a qualified name or path:line:\n  class Orders.MainViewModel  src/Orders/MainViewModel.cs:2\n  class Shell.MainViewModel  src/Shell/MainViewModel.cs:2\n"),
            await SymbolReader.ReadAsync(ws, "MainViewModel", null, default));
        Assert.Equal("No symbol 'OrdrService'. Closest: OrderService (class, src/Core/Services/OrderService.cs:2)",
            (await Assert.ThrowsAsync<WpfNavException>(() => SymbolReader.ReadAsync(ws, "OrdrService", null, default))).Message);
        Assert.Contains("also declared in: src/Orders/Views/OrderEditView.xaml:1\n", await SymbolReader.ReadAsync(ws, "OrderEditView", null, default));
    }
}
