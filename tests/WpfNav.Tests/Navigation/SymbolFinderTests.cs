namespace WpfNav.Tests.Navigation;

public class SymbolFinderTests
{
    [Fact]
    public async Task Renders_matches_with_partial_parts()
    {
        using var repo = SampleRepo.Create();
        using var ws = TestWorkspace.Started(repo);
        var text = await SymbolFinder.FindAsync(ws, new SymbolQuery("OrderEditView"), 20, 0, default);
        Assert.Contains("2 matches for \"OrderEditView\"\n", text);
        Assert.Contains("public partial class OrderEditView : UserControl  [Orders.Views]  src/Orders/Views/OrderEditView.xaml.cs:2  (+1 part: src/Orders/Views/OrderEditView.xaml:1)\n", text);
        Assert.Contains("public partial class OrderEditViewModel : ViewModelBase  [Orders.ViewModels]  src/Orders/ViewModels/OrderEditViewModel.cs:2\n", text);
    }

    [Fact]
    public async Task No_matches_suggest_close_names_and_paging_uses_offset()
    {
        using var repo = SampleRepo.Create();
        using var ws = TestWorkspace.Started(repo);
        Assert.Contains("0 matches for \"OrdrService\". Closest: OrderService (class, src/Core/Services/OrderService.cs:2)\n",
            await SymbolFinder.FindAsync(ws, new SymbolQuery("OrdrService"), 20, 0, default));
        Assert.Matches(@"… \d+ more — narrow with project=/path= or pass offset=3\n$",
            await SymbolFinder.FindAsync(ws, new SymbolQuery("*"), 3, 0, default));
    }

    static TempRepo Widgets() => TempRepo.Create(Enumerable.Range(0, 300).Select(i =>
        ($"src/F{i:000}.cs", $"namespace Demo.Area{i:000};\npublic class Widget{i:000}ViewModel {{ }}\n")).ToArray());

    [Theory, InlineData(256), InlineData(700), InlineData(null)]
    public async Task Every_budget_bounds_the_whole_result_and_the_next_offset_follows_what_was_shown(int? budget)
    {
        using var repo = Widgets();
        using var ws = TestWorkspace.Started(repo);
        var text = await SymbolFinder.FindAsync(ws, new SymbolQuery("Widget"), 300, 0, budget, default);
        Assert.True(OutputWriter.EstimateTokens(text) <= (budget ?? Limits.FindSymbol));
        Assert.StartsWith(TestText.Lf($"root: {repo.Root}\n300 matches for \"Widget\"\n"), text);
        var shown = text.Split('\n').Count(l => l.Contains("  src/F"));
        Assert.True(shown > 0);
        Assert.EndsWith($"… {300 - shown} more — narrow with project=/path= or pass offset={shown}\n", text);
        var next = await SymbolFinder.FindAsync(ws, new SymbolQuery("Widget"), 300, shown, budget, default);
        Assert.Contains($"Widget{shown:000}ViewModel", next);
    }

    [Fact]
    public async Task Small_budgets_keep_no_match_suggestions_and_long_queries_bounded()
    {
        using var repo = Widgets();
        using var ws = TestWorkspace.Started(repo);
        var text = await SymbolFinder.FindAsync(ws, new SymbolQuery("Gadget" + new string('x', 3000)), 20, 0, Budget.Minimum, default);
        Assert.True(OutputWriter.EstimateTokens(text) <= Budget.Minimum);
        Assert.Contains("0 matches for \"Gadgetxxx", text);
    }

    [Fact]
    public async Task Small_budgets_keep_the_partial_index_warning()
    {
        using var repo = TempRepo.Create(("src/A.cs", "class A { }"));
        using var idle = new Workspace(new RepoRoot(repo.Root), [], indexWait: TimeSpan.Zero, watch: false);
        var text = await SymbolFinder.FindAsync(idle, new SymbolQuery("A"), 20, 0, Budget.Minimum, default);
        Assert.Contains("results may be incomplete", text);
        Assert.True(OutputWriter.EstimateTokens(text) <= Budget.Minimum);
    }

    [Fact]
    public async Task Budgets_outside_the_range_are_rejected()
    {
        using var repo = TempRepo.Create(("src/A.cs", "class A { }"));
        using var ws = TestWorkspace.Started(repo);
        var ex = await Assert.ThrowsAsync<WpfNavException>(() => SymbolFinder.FindAsync(ws, new SymbolQuery("A"), 20, 0, 255, default));
        Assert.Equal("max_tokens must be between 256 and 2000; got 255. Omit it for the default (2000).", ex.Message);
    }

    [Fact]
    public async Task Unknown_project_echo_is_bounded()
    {
        using var repo = TempRepo.Create(("src/A.cs", "class A { }"));
        using var ws = TestWorkspace.Started(repo);
        var ex = await Assert.ThrowsAsync<WpfNavException>(() => SymbolFinder.FindAsync(ws, new SymbolQuery("A", Project: new string('p', 5000)), 20, 0, default));
        Assert.True(ex.Message.Length < 200, ex.Message);
        Assert.StartsWith("Unknown project 'ppp", ex.Message);
    }
}
