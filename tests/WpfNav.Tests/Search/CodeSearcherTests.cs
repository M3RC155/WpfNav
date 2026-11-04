namespace WpfNav.Tests.Search;

public class CodeSearcherTests
{
    static TempRepo Repo() => TempRepo.Create(
        ("src/Core/Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"),
        ("src/Core/OrderService.cs", """
            namespace Core;
            public class OrderService
            {
                public void Create(Order order)
                {
                    var c = Get(order.CustomerId);
                    Log(order.CustomerId);
                }
                public void Delete(int customerId) => Remove(customerId);
                public int CustomerIdLength = 0;
            }
            """),
        ("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>"),
        ("src/App/OrderView.xaml", """
            <UserControl xmlns="p" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <StackPanel>
                    <TextBox x:Name="CustomerIdBox" Text="{Binding Order.CustomerId}" />
                </StackPanel>
            </UserControl>
            """),
        ("tests/T/T.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup></Project>"),
        ("tests/T/OrderServiceTests.cs", """
            public class OrderServiceTests
            {
                void Creates() => new OrderService().Create(new Order { CustomerId = 1 });
            }
            """));

    [Fact]
    public async Task Groups_hits_by_file_and_member_with_one_sample_each()
    {
        using var repo = Repo();
        using var ws = TestWorkspace.Started(repo);
        Assert.Equal(TestText.Lf($$"""
            root: {{repo.Root}}
            "CustomerId": 5 hits in 3 files (4 members)
            src/Core/OrderService.cs (3)
              OrderService.Create [4-8]: 2 hits — L6: var c = Get(order.CustomerId);
              OrderService.CustomerIdLength [10]: L10: public int CustomerIdLength = 0;
            src/App/OrderView.xaml (1)
              TextBox x:Name=CustomerIdBox [3]: L3: <TextBox x:Name="CustomerIdBox" Text="{Binding Order.CustomerId}" />
            tests/T/OrderServiceTests.cs (1)
              OrderServiceTests.Creates [3]: L3: void Creates() => new OrderService().Create(new Order { CustomerId = 1 });

            """), await CodeSearcher.SearchAsync(ws, new SearchRequest("CustomerId"), default));
    }

    [Fact]
    public async Task Options_narrow_the_search()
    {
        using var repo = Repo();
        using var ws = TestWorkspace.Started(repo);
        async Task<string> Summary(SearchRequest r) => (await CodeSearcher.SearchAsync(ws, r, default)).Split('\n')[1];
        Assert.Equal("\"CustomerId\": 4 hits in 3 files (3 members)", await Summary(new("CustomerId", WholeWord: true)));
        Assert.Equal("\"customerid\": 6 hits in 3 files (5 members)", await Summary(new("customerid", CaseSensitive: false)));
        Assert.Equal("\"CustomerId\": 1 hit in 1 file (1 member)", await Summary(new("CustomerId", FileKind: "xaml")));
        Assert.Equal("\"CustomerId\": 3 hits in 1 file (2 members)", await Summary(new("CustomerId", Project: "Core")));
        Assert.Equal("\"CustomerId\": 1 hit in 1 file (1 member)", await Summary(new("CustomerId", Path: "tests")));
        Assert.Equal("\"CustomerId\": 1 hit in 1 file (1 member)", await Summary(new("CustomerId", Path: "tests/")));
        Assert.Equal("\"Get(order.\": 1 hit in 1 file (1 member)", await Summary(new("Get(order.", Literal: true)));
    }

    [Fact]
    public async Task Bad_patterns_kinds_and_projects_are_explained()
    {
        using var repo = Repo();
        using var ws = TestWorkspace.Started(repo);
        var regex = await Assert.ThrowsAsync<WpfNavException>(() => CodeSearcher.SearchAsync(ws, new("a("), default));
        Assert.StartsWith("Invalid regex: ", regex.Message);
        Assert.EndsWith(" Pass literal=true to search for the text as-is.", regex.Message);
        Assert.Equal("Unknown file_kind 'json'. Use cs, xaml or all.",
            (await Assert.ThrowsAsync<WpfNavException>(() => CodeSearcher.SearchAsync(ws, new("x", FileKind: "json"), default))).Message);
        Assert.Equal("Unknown project 'Cor'. Closest: Core",
            (await Assert.ThrowsAsync<WpfNavException>(() => CodeSearcher.SearchAsync(ws, new("x", Project: "Cor"), default))).Message);
    }

    [Fact]
    public async Task Pages_by_group_and_survives_catastrophic_patterns()
    {
        using var repo = TempRepo.Create(Enumerable.Range(0, 20).Select(i => ($"src/F{i:00}.cs", $"class F{i:00} {{ void M() {{ Hit(); }} }}"))
            .Append(("src/Slow.cs", string.Join(" ", Enumerable.Repeat("aaaa", 40)) + "!")).ToArray());
        using var ws = TestWorkspace.Started(repo);
        Assert.EndsWith("… 5 more — narrow with project=/path= or pass offset=15\n", await CodeSearcher.SearchAsync(ws, new("Hit"), default));
        Assert.EndsWith("(regex timed out in 1 file)", (await CodeSearcher.SearchAsync(ws, new(@"^(\w+\s?)*$"), default)).Split('\n')[1]);
    }

    [Theory, InlineData(256), InlineData(700), InlineData(null)]
    public async Task Every_budget_bounds_the_whole_result_and_the_next_offset_follows_what_was_shown(int? budget)
    {
        using var repo = TempRepo.Create(Enumerable.Range(0, 120).Select(i =>
            ($"src/Area{i:000}/F{i:000}.cs", $"class F{i:000}\n{{\n    void First() {{ Hit(); }}\n    void Second() {{ Hit(); }}\n}}\n")).ToArray());
        using var ws = TestWorkspace.Started(repo);
        var text = await CodeSearcher.SearchAsync(ws, new SearchRequest("Hit", Limit: 500) { MaxTokens = budget }, default);
        Assert.True(OutputWriter.EstimateTokens(text) <= (budget ?? Limits.SearchCode));
        Assert.StartsWith(TestText.Lf($"root: {repo.Root}\n\"Hit\": 240 hits in 120 files (240 members)\n"), text);
        var shown = text.Split('\n').Count(l => l.StartsWith("  F", StringComparison.Ordinal));
        Assert.True(shown > 0);
        Assert.EndsWith($"… {240 - shown} more — narrow with project=/path= or pass offset={shown}\n", text);
    }

    [Fact]
    public async Task Long_patterns_and_bad_inputs_stay_bounded()
    {
        using var repo = Repo();
        using var ws = TestWorkspace.Started(repo);
        var text = await CodeSearcher.SearchAsync(ws, new SearchRequest(new string('q', 3000), Literal: true) { MaxTokens = Budget.Minimum }, default);
        Assert.True(OutputWriter.EstimateTokens(text) <= Budget.Minimum);
        Assert.Contains("\"qqq", text);
        var regex = await Assert.ThrowsAsync<WpfNavException>(() => CodeSearcher.SearchAsync(ws, new(new string('(', 3000)), default));
        Assert.True(regex.Message.Length < 400, regex.Message);
        Assert.EndsWith(" Pass literal=true to search for the text as-is.", regex.Message);
        var project = await Assert.ThrowsAsync<WpfNavException>(() => CodeSearcher.SearchAsync(ws, new("x", Project: new string('p', 3000)), default));
        Assert.True(project.Message.Length < 200, project.Message);
        Assert.Equal("max_tokens must be between 256 and 2500; got 2501. Omit it for the default (2500).",
            (await Assert.ThrowsAsync<WpfNavException>(() => CodeSearcher.SearchAsync(ws, new("x") { MaxTokens = 2501 }, default))).Message);
    }
}
