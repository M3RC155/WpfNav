using WpfNav.Tests.Parsing;

namespace WpfNav.Tests.Navigation;

public class OutlineBuilderTests
{
    public const string Vm = """
        namespace Contoso.Orders;
        public partial class OrderEditViewModel : ViewModelBase
        {
            private readonly IOrderService _orders;
            private int _retries;
            [ObservableProperty] private Order? _order;
            public OrderEditViewModel(IOrderService orders) => _orders = orders;
            [RelayCommand]
            private async Task SaveAsync()
            {
            }
            public enum Mode { A, B, C, D, E, F, G, H, I, J }
        }
        """;

    static Workspace Ws(TempRepo repo) => new(new RepoRoot(repo.Root), []);

    [Fact]
    public void Renders_a_csharp_file()
    {
        using var repo = TempRepo.Create(("src/Orders/OrderEditViewModel.cs", Vm));
        Assert.Equal(TestText.Lf($$"""
            root: {{repo.Root}}
            src/Orders/OrderEditViewModel.cs (13 lines)
            namespace Contoso.Orders
              public partial class OrderEditViewModel : ViewModelBase [2-13]
                fields: _orders, _retries
                [ObservableProperty] Order? _order [6]
                public ctor(IOrderService orders) [7]
                [RelayCommand] async Task SaveAsync() [9-11]
                public enum Mode { A, B, C, D, E, F, G, H, … (10) } [12]

            """), OutlineBuilder.Build(Ws(repo), new[] { "src/Orders/OrderEditViewModel.cs" }));
    }

    [Fact]
    public void Renders_a_xaml_file_with_code_behind_events()
    {
        using var repo = TempRepo.Create(("src/V.xaml", XamlOutlinerTests.View),
            ("src/V.xaml.cs", "partial class V { void OnCancel(object s, EventArgs e) { } }"));
        var text = OutlineBuilder.Build(Ws(repo), new[] { "src/V.xaml" });
        Assert.Contains("src/V.xaml (28 lines)  UserControl  x:Class=Contoso.Orders.Views.OrderEditView\n", text);
        Assert.Contains("  code-behind: src/V.xaml.cs\n  DataContext: OrderEditViewModel (design)\n", text);
        Assert.Contains("  resources:\n    OrderLineTemplate  DataTemplate DataType=vm:OrderLine [12-14]\n    (implicit)  Style TargetType=Button [15]\n", text);
        Assert.Contains("  merged: /Themes/Colors.xaml\n", text);
        Assert.Contains("  Grid [18-27]\n    StackPanel [20-23]\n      Button \"Save\" Command={Binding SaveCommand} [21]\n      Button \"Cancel\" Click=OnCancel [22]\n", text);
    }

    [Fact]
    public void A_folder_lists_one_line_per_type_by_default()
    {
        using var repo = TempRepo.Create(("src/Orders/OrderEditViewModel.cs", Vm), ("src/Orders/V.xaml", XamlOutlinerTests.View));
        var text = OutlineBuilder.Build(Ws(repo), new[] { "src/Orders" });
        Assert.Contains("src/Orders/OrderEditViewModel.cs (13 lines)\n  public partial class OrderEditViewModel : ViewModelBase [2-13]\n    public enum Mode [12]\n", text);
        Assert.Contains("src/Orders/V.xaml (28 lines)  UserControl  x:Class=Contoso.Orders.Views.OrderEditView\n", text);
        Assert.DoesNotContain("SaveAsync", text);
    }

    [Fact]
    public void Many_files_are_paged_with_offset()
    {
        using var repo = TempRepo.Create(Enumerable.Range(0, 150).Select(i =>
            ($"src/F{i:000}.cs", $"public class VeryLongClassNameNumber{i:000}ForPaging : SomeBaseType {{ }}")).ToArray());
        var first = OutlineBuilder.Build(Ws(repo), new[] { "src" });
        Assert.True(OutputWriter.EstimateTokens(first) <= Limits.Outline);
        var m = Regex.Match(first, @"… (\d+) more files; repeat the same paths/level with offset=(\d+), entry_offset=0\n$");
        var next = int.Parse(m.Groups[2].Value);
        Assert.Equal(150 - next, int.Parse(m.Groups[1].Value));
        Assert.Contains($"src/F{next:000}.cs", OutlineBuilder.Build(Ws(repo), new[] { "src" }, offset: next));
    }

    static string HugeClass(int methods = 600) =>
        $"class Huge\n{{\n{string.Concat(Enumerable.Range(0, methods).Select(i => $"    public void Method{i:000}(int a, string b) {{ }}\n"))}}}\n";

    static IEnumerable<string> HugeEntries(int methods = 600) =>
        Enumerable.Range(0, methods).Select(i => $"    public void Method{i:000}(int a, string b) [{i + 3}]").Prepend($"  class Huge [1-{methods + 3}]");

    [Fact]
    public void A_huge_file_stops_at_the_limit_with_a_continuation()
    {
        using var repo = TempRepo.Create(("src/Huge.cs", HugeClass()));
        var text = OutlineBuilder.Build(Ws(repo), new[] { "src/Huge.cs" });
        Assert.True(OutputWriter.EstimateTokens(text) <= Limits.Outline);
        var m = Regex.Match(text, @"\n… (\d+) more entries in this file; repeat the same paths/level with offset=0, entry_offset=(\d+)\n$");
        Assert.True(m.Success, text[^200..]);
        Assert.Equal(601, int.Parse(m.Groups[1].Value) + int.Parse(m.Groups[2].Value));
    }

    [Fact]
    public void A_cut_first_file_continues_inside_itself_before_the_files_after_it()
    {
        using var repo = TempRepo.Create(("src/Huge.cs", HugeClass()), ("src/Small.cs", "class Small { }"));
        var text = OutlineBuilder.Build(Ws(repo), new[] { "src/Huge.cs", "src/Small.cs" });
        Assert.True(OutputWriter.EstimateTokens(text) <= Limits.Outline);
        Assert.Matches(@"\n… \d+ more entries in this file and 1 more file; repeat the same paths/level with offset=0, entry_offset=\d+\n$", text);
        Assert.DoesNotContain("Small", text);
    }

    /// <summary>Follows continuation footers from the start until a page has none, checking every page's budget and progress.</summary>
    static List<string> AllPages(Workspace ws, string[] paths, string? level, int? budget)
    {
        var pages = new List<string>();
        var position = (Offset: 0, Entry: 0);
        for (var guard = 0; guard < 1000; guard++)
        {
            var page = OutlineBuilder.Build(ws, paths, level, position.Offset, position.Entry, budget);
            Assert.True(OutputWriter.EstimateTokens(page) <= (budget ?? Limits.Outline), $"page {guard}: {OutputWriter.EstimateTokens(page)} tokens");
            pages.Add(page);
            var m = Regex.Match(page, @"repeat the same paths/level with offset=(\d+), entry_offset=(\d+)\n$");
            if (!m.Success) return pages;
            var next = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            Assert.True(next.CompareTo(position) > 0, $"page {guard} did not advance from {position}");
            position = next;
        }
        throw new Xunit.Sdk.XunitException("outline did not end within 1000 pages");
    }

    /// <summary>Every body line of the pages: no root, file headers, repeated context or footers.</summary>
    static List<string> Body(IEnumerable<string> pages, Func<string, bool> isHeader) =>
        pages.SelectMany(p => p.Split('\n'))
            .Where(l => l.Length > 0 && !l.StartsWith("root: ", StringComparison.Ordinal) && !l.StartsWith("… ", StringComparison.Ordinal)
                && !l.EndsWith(" (continued)", StringComparison.Ordinal) && !isHeader(l))
            .ToList();

    [Theory, InlineData(256), InlineData(1000), InlineData(null)]
    public void Hundreds_of_members_reconstruct_once_each_at_every_budget(int? budget)
    {
        using var repo = TempRepo.Create(("src/Huge.cs", HugeClass()));
        var pages = AllPages(Ws(repo), ["src/Huge.cs"], null, budget);
        Assert.True(pages.Count > 1);
        Assert.All(pages, p => Assert.Contains("\nsrc/Huge.cs (603 lines)\n", p));
        Assert.All(pages.Skip(1), p => Assert.Contains("\nsrc/Huge.cs (603 lines)\n  class Huge [1-603] (continued)\n    public void Method", p));
        Assert.Equal(HugeEntries(), Body(pages, l => l == "src/Huge.cs (603 lines)"));
    }

    [Theory, InlineData(256), InlineData(null)]
    public void A_large_middle_file_is_paged_in_place_between_its_neighbours(int? budget)
    {
        using var repo = TempRepo.Create(("src/A.cs", "class Alpha { void One() { } }"), ("src/B.cs", HugeClass()), ("src/C.cs", "class Gamma { void Two() { } }"));
        var pages = AllPages(Ws(repo), ["src"], "members", budget);
        var expected = new[] { "  class Alpha [1]", "    void One() [1]" }.Concat(HugeEntries()).Concat(["  class Gamma [1]", "    void Two() [1]"]);
        Assert.Equal(expected, Body(pages, l => Regex.IsMatch(l, @"^src/[ABC]\.cs \(\d+ lines\)$")));
    }

    [Fact]
    public void Overlapping_paths_render_each_file_once_in_requested_then_folder_order()
    {
        using var repo = TempRepo.Create(("src/b.cs", "class Beta { }"), ("src/A.cs", "class Alpha { }"), ("src/a2.cs", "class Alpha2 { }"), ("lib/Z.cs", "class Zed { }"));
        var text = OutlineBuilder.Build(Ws(repo), new[] { "lib/Z.cs", "src", "SRC/b.cs", "src/A.cs", "lib" });
        var headers = text.Split('\n').Where(l => l.EndsWith(" lines)", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "lib/Z.cs (1 lines)", "src/A.cs (1 lines)", "src/a2.cs (1 lines)", "src/b.cs (1 lines)" }, headers);
    }

    [Fact]
    public void A_page_that_starts_inside_nested_types_repeats_their_context()
    {
        var methods = string.Concat(Enumerable.Range(0, 300).Select(i => $"            public void Method{i:000}() {{ }}\n"));
        using var repo = TempRepo.Create(("src/Nested.cs",
            $"namespace Demo\n{{\n    public class Outer\n    {{\n        public class Inner\n        {{\n{methods}        }}\n    }}\n}}\n"));
        var pages = AllPages(Ws(repo), ["src/Nested.cs"], null, Budget.Minimum);
        Assert.StartsWith("src/Nested.cs (309 lines)\nnamespace Demo\n  public class Outer [3-308]\n    public class Inner [5-307]\n", pages[0].Split('\n', 2)[1]);
        Assert.All(pages.Skip(1), p => Assert.StartsWith(
            "src/Nested.cs (309 lines)\nnamespace Demo (continued)\n  public class Outer [3-308] (continued)\n    public class Inner [5-307] (continued)\n      public void Method",
            p.Split('\n', 2)[1]));
        Assert.Equal(302, Body(pages, l => l == "src/Nested.cs (309 lines)" || l == "namespace Demo").Count);
    }

    [Fact]
    public void Offsets_at_and_past_the_end_advance_or_finish_and_negative_offsets_are_rejected()
    {
        using var repo = TempRepo.Create(("src/A.cs", "class Alpha { void One() { } }"), ("src/B.cs", "class Beta { }"));
        var ws = Ws(repo);
        string[] paths = ["src/A.cs", "src/B.cs"];
        Assert.Equal(TestText.Lf($"root: {repo.Root}\nsrc/B.cs (1 lines)\n  class Beta [1]\n"), OutlineBuilder.Build(ws, paths, offset: 0, entryOffset: 2));
        Assert.Equal(TestText.Lf($"root: {repo.Root}\nsrc/B.cs (1 lines)\n  class Beta [1]\n"), OutlineBuilder.Build(ws, paths, offset: 0, entryOffset: 99));
        Assert.Equal(TestText.Lf($"root: {repo.Root}\nno more outline entries at offset=1, entry_offset=1 (2 files in total)\n"), OutlineBuilder.Build(ws, paths, offset: 1, entryOffset: 1));
        Assert.Equal(TestText.Lf($"root: {repo.Root}\nno more outline entries at offset=2, entry_offset=0 (2 files in total)\n"), OutlineBuilder.Build(ws, paths, offset: 2));
        Assert.Equal(TestText.Lf($"root: {repo.Root}\nno more outline entries at offset=7, entry_offset=3 (2 files in total)\n"), OutlineBuilder.Build(ws, paths, offset: 7, entryOffset: 3));
        Assert.Equal("offset must be zero or more; got -1.", Assert.Throws<WpfNavException>(() => OutlineBuilder.Build(ws, paths, offset: -1)).Message);
        Assert.Equal("entry_offset must be zero or more; got -2.", Assert.Throws<WpfNavException>(() => OutlineBuilder.Build(ws, paths, entryOffset: -2)).Message);
        Assert.Equal("max_tokens must be between 256 and 3000; got 100. Omit it for the default (3000).",
            Assert.Throws<WpfNavException>(() => OutlineBuilder.Build(ws, paths, maxTokens: 100)).Message);
    }

    [Theory, InlineData(256), InlineData(null)]
    public void Oversized_grouped_fields_split_into_bounded_rows_that_keep_every_name(int? budget)
    {
        var fields = string.Concat(Enumerable.Range(0, 200).Select(i => $"    private int _field{i:000};\n"));
        using var repo = TempRepo.Create(("src/Fields.cs", $"class Fields\n{{\n{fields}}}\n"));
        var pages = AllPages(Ws(repo), ["src/Fields.cs"], null, budget);
        var rows = Body(pages, l => l == "src/Fields.cs (203 lines)").Skip(1).ToList();
        Assert.True(rows.Count > 1);
        Assert.All(rows, r => Assert.True(r.StartsWith("    fields: ", StringComparison.Ordinal) && r.Length <= 220, r));
        Assert.Equal(Enumerable.Range(0, 200).Select(i => $"_field{i:000}"), rows.SelectMany(r => r["    fields: ".Length..].Split(", ")));
    }

    [Theory, InlineData(256), InlineData(null)]
    public void Xaml_children_resources_and_dictionaries_past_the_old_cap_are_all_reachable(int? budget)
    {
        var resources = string.Concat(Enumerable.Range(0, 40).Select(i => $"    <SolidColorBrush x:Key=\"Brush{i:00}\" Color=\"Red\" />\n"));
        var merged = string.Concat(Enumerable.Range(0, 30).Select(i => $"      <ResourceDictionary Source=\"/Themes/Dictionary{i:00}.xaml\" />\n"));
        var view = XamlOutlinerTests.Wide().Replace("<StackPanel xmlns=\"p\">\n",
            "<StackPanel xmlns=\"p\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n  <StackPanel.Resources>\n  <ResourceDictionary>\n    <ResourceDictionary.MergedDictionaries>\n"
            + merged + "    </ResourceDictionary.MergedDictionaries>\n" + resources + "  </ResourceDictionary>\n  </StackPanel.Resources>\n");
        using var repo = TempRepo.Create(("src/Wide.xaml", view));
        var pages = AllPages(Ws(repo), ["src/Wide.xaml"], null, budget);
        var body = Body(pages, l => l.StartsWith("src/Wide.xaml (", StringComparison.Ordinal) || l == "  resources:");
        Assert.Equal(Enumerable.Range(0, 40).Select(i => $"Brush{i:00}"), body.Where(l => l.Contains("SolidColorBrush")).Select(l => l.Trim().Split(' ')[0]));
        Assert.Equal(Enumerable.Range(0, 30).Select(i => $"/Themes/Dictionary{i:00}.xaml"),
            body.Where(l => l.StartsWith("  merged: ", StringComparison.Ordinal)).SelectMany(l => l["  merged: ".Length..].Split(", ")));
        var buttons = body.Where(l => l.Contains("Button")).Select(l => Regex.Match(l, "\"(B..-..)\"").Groups[1].Value).ToList();
        Assert.Equal(Enumerable.Range(0, 25).SelectMany(i => Enumerable.Range(0, 22).Select(j => $"B{i:00}-{j:00}")), buttons);
        Assert.Equal(25, body.Count(l => l.StartsWith("  WrapPanel x:Name=Panel", StringComparison.Ordinal)));
        Assert.DoesNotContain(body, l => l.Contains("… ") && l.Contains(" more"));
    }

    [Fact]
    public void Malformed_xaml_pages_keep_the_error_with_the_file_header()
    {
        var buttons = string.Concat(Enumerable.Range(0, 120).Select(j => $"    <Button Content=\"Button number {j:000}\" />\n"));
        using var repo = TempRepo.Create(("src/Bad.xaml", $"<Window xmlns=\"p\">\n  <StackPanel>\n{buttons}</Window>\n"));
        var pages = AllPages(Ws(repo), ["src/Bad.xaml"], null, Budget.Minimum);
        Assert.True(pages.Count > 1);
        Assert.All(pages, p => Assert.Matches(@"\nsrc/Bad\.xaml \(123 lines\)  Window\n  XML error at line 123: ", p));
    }

    [Theory]
    [InlineData("../x.cs", "Path is outside the repository root: ../x.cs")]
    [InlineData("src/Nope.cs", "File not found: src/Nope.cs")]
    [InlineData("README.md", "Unsupported file type: README.md (outline takes .cs and .xaml files or folders)")]
    public void Bad_paths_raise_actionable_errors(string path, string message)
    {
        using var repo = TempRepo.Create(("README.md", "x"));
        Assert.Equal(message, Assert.Throws<WpfNavException>(() => OutlineBuilder.Build(Ws(repo), new[] { path })).Message);
    }

    [Fact]
    public void Unknown_level_is_rejected()
    {
        using var repo = TempRepo.Create(("src/A.cs", "class A { }"));
        Assert.Equal("Unknown level 'all'. Use types or members.",
            Assert.Throws<WpfNavException>(() => OutlineBuilder.Build(Ws(repo), new[] { "src/A.cs" }, "all")).Message);
    }
}
