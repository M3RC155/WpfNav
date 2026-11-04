namespace WpfNav.Tests.Corpus;

/// <summary>Runs every tool against a generated WPF codebase the size of a large sample repository (see <see cref="WpfCorpus"/>).</summary>
public class CorpusSmokeTests
{
    static void Within(int limit, string text) =>
        Assert.True(OutputWriter.EstimateTokens(text) <= limit, $"{OutputWriter.EstimateTokens(text)} > {limit}");

    /// <summary>The result fits the tool's limit and was cut short to fit, so the corpus really exercised that limit.</summary>
    static void Capped(int limit, string text, string note)
    {
        Within(limit, text);
        Assert.Contains(note, text);
    }

    [Fact]
    public async Task Indexes_the_corpus_fast_and_keeps_every_tool_within_its_limit()
    {
        using var repo = WpfCorpus.Create();
        using var ws = new Workspace(new RepoRoot(repo.Root), [], watch: false);
        var sw = Stopwatch.StartNew();
        ws.Start();
        var snap = await ws.GetIndexAsync(default);
        Assert.True(snap.Status.IsReady && sw.Elapsed < TimeSpan.FromSeconds(5), $"cold index took {sw.Elapsed}");
        Assert.True(snap.Status.Total >= 2_600, $"only {snap.Status.Total} files indexed");
        var gallery = await OverviewBuilder.BuildAsync(ws, WpfCorpus.GalleryProject, default);
        Assert.Contains("project: Gallery  WPF app  net10.0-windows", gallery);
        Assert.Contains("CommunityToolkit.Mvvm 8.2.2", gallery);
        Assert.Contains("Microsoft.Extensions.Hosting 8.0.0", gallery);
        Within(Limits.Overview, await OverviewBuilder.BuildAsync(ws, null, default));
        Capped(Limits.Outline, OutlineBuilder.Build(ws, new[] { WpfCorpus.ConverterFile }), "more entries in this file");
        Capped(Limits.Outline, OutlineBuilder.Build(ws, new[] { WpfCorpus.DesignerFile }), "more entries in this file");
        Capped(Limits.FindSymbol, await SymbolFinder.FindAsync(ws, new SymbolQuery("*ViewModel"), 20, 0, default), "pass offset=20");
        Capped(Limits.ReadSymbol, await SymbolReader.ReadAsync(ws, WpfCorpus.ConverterType, null, default), "line_offset=0");
        Capped(Limits.SearchCode, await CodeSearcher.SearchAsync(ws, new SearchRequest("Dispatcher"), default), "more —");
    }
}
