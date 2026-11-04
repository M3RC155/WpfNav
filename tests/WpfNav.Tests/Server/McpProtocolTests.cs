using System.Text.Json;

namespace WpfNav.Tests.Server;

public class McpProtocolTests
{
    static readonly Dictionary<string, (int Ceiling, string[] Required, string[] Added)> Contracts = new()
    {
        ["overview"] = (Limits.Overview, [], ["max_tokens"]),
        ["outline"] = (Limits.Outline, ["paths"], ["entry_offset", "max_tokens"]),
        ["find_symbol"] = (Limits.FindSymbol, ["query"], ["max_tokens"]),
        ["read_symbol"] = (Limits.ReadSymbol, ["symbol"], ["line_offset", "line_limit", "column_offset", "max_tokens"]),
        ["search_code"] = (Limits.SearchCode, ["pattern"], ["max_tokens"]),
    };

    static Dictionary<string, object?> Args(string tool, int? maxTokens = null)
    {
        var args = tool switch
        {
            "overview" => new Dictionary<string, object?>(),
            "outline" => new() { ["paths"] = new[] { "src/A.cs" } },
            "find_symbol" => new() { ["query"] = "A" },
            "read_symbol" => new() { ["symbol"] = "A.Run" },
            _ => new() { ["pattern"] = "Run" },
        };
        if (maxTokens is not null) args["max_tokens"] = maxTokens;
        return args;
    }

    [Fact]
    public async Task Lists_the_tools_with_descriptions_and_sends_instructions()
    {
        using var repo = TempRepo.Create(("src/A.cs", "public class A { public void Run() { } }"));
        await using var h = await ServerHarness.StartAsync(repo.Root);
        var tools = await h.Client.ListToolsAsync();
        Assert.Equal(new[] { "find_symbol", "outline", "overview", "read_symbol", "search_code" }, tools.Select(t => t.Name).Order());
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.Equal(ServerSetup.Instructions, h.Client.ServerInstructions);
    }

    [Fact]
    public async Task Transmitted_guidance_is_conditional_and_names_continuations_and_budgets()
    {
        using var repo = TempRepo.Create(("src/A.cs", "public class A { public void Run() { } }"));
        await using var h = await ServerHarness.StartAsync(repo.Root);
        var instructions = h.Client.ServerInstructions!;
        var tools = await h.Client.ListToolsAsync();
        var all = instructions + "\n" + string.Join("\n", tools.Select(t => t.Description));
        foreach (var mandatory in new[] { "start a task with", "Call it first", "before Read on any", "before reading any", "run `outline` before" })
            Assert.DoesNotContain(mandatory, all, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Use `overview` when repository or project structure is unfamiliar.", instructions);
        Assert.Contains("Read a known target directly with `read_symbol`", instructions);
        Assert.Contains("Use `outline` to inspect structure or choose members.", instructions);
        Assert.Contains("follow continuation positions", instructions);
        Assert.Contains("smaller max_tokens", instructions);
        Assert.Contains("Paths are relative to the root shown in results", instructions);
        Assert.Contains("entry_offset", tools.Single(t => t.Name == "outline").Description);
        Assert.Contains("line_offset", tools.Single(t => t.Name == "read_symbol").Description);
    }

    [Fact]
    public async Task Exactly_five_read_only_tools_whose_new_parameters_are_optional_and_described()
    {
        using var repo = TempRepo.Create(("src/A.cs", "public class A { public void Run() { } }"));
        await using var h = await ServerHarness.StartAsync(repo.Root);
        var tools = await h.Client.ListToolsAsync();
        Assert.Equal(5, tools.Count);
        foreach (var tool in tools)
        {
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint, tool.Name);
            var (_, required, added) = Contracts[tool.Name];
            var schema = tool.JsonSchema;
            var requiredNames = schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()!).Order().ToArray() : [];
            Assert.Equal(required.Order(), requiredNames);
            var properties = schema.GetProperty("properties");
            foreach (var name in added)
            {
                Assert.True(properties.TryGetProperty(name, out var p), $"{tool.Name}.{name} missing");
                Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("description").GetString()), $"{tool.Name}.{name} undescribed");
            }
        }
    }

    [Theory]
    [InlineData("overview"), InlineData("outline"), InlineData("find_symbol"), InlineData("read_symbol"), InlineData("search_code")]
    public async Task Every_tool_honors_the_smallest_budget_and_keeps_its_default(string tool)
    {
        using var repo = TempRepo.Create(("src/A.cs", "public class A { public void Run() { } }"));
        await using var h = await ServerHarness.StartAsync(repo.Root);
        var small = await h.Client.CallToolAsync(tool, Args(tool, Budget.Minimum));
        Assert.False(small.IsError == true, small.Text());
        Assert.StartsWith($"root: {repo.Root}\n", small.Text());
        Assert.True(OutputWriter.EstimateTokens(small.Text()) <= Budget.Minimum);
        var normal = await h.Client.CallToolAsync(tool, Args(tool));
        Assert.False(normal.IsError == true, normal.Text());
        Assert.True(OutputWriter.EstimateTokens(normal.Text()) <= Contracts[tool].Ceiling);
    }

    [Theory]
    [InlineData("overview"), InlineData("outline"), InlineData("find_symbol"), InlineData("read_symbol"), InlineData("search_code")]
    public async Task Budgets_below_the_minimum_or_above_the_ceiling_are_error_results(string tool)
    {
        using var repo = TempRepo.Create(("src/A.cs", "public class A { public void Run() { } }"));
        await using var h = await ServerHarness.StartAsync(repo.Root);
        var ceiling = Contracts[tool].Ceiling;
        foreach (var bad in new[] { 255, 0, -5, ceiling + 1 })
        {
            var r = await h.Client.CallToolAsync(tool, Args(tool, bad));
            Assert.True(r.IsError == true, $"{tool} max_tokens={bad}");
            Assert.Contains($"max_tokens must be between 256 and {ceiling}; got {bad}. Omit it for the default ({ceiling}).", r.Text());
        }
    }

    [Fact]
    public async Task A_known_target_is_read_directly_without_overview_or_outline()
    {
        using var repo = TempRepo.Create(("src/A.cs", "public class A { public void Run() { } }"));
        await using var h = await ServerHarness.StartAsync(repo.Root);
        var r = await h.Client.CallToolAsync("read_symbol", new Dictionary<string, object?> { ["symbol"] = "A.Run", ["max_tokens"] = 300 });
        Assert.False(r.IsError == true, r.Text());
        Assert.Contains("src/A.cs:1  A.Run\n1\tpublic class A { public void Run() { } }\n", r.Text());
    }

    [Fact]
    public async Task Long_echoes_in_errors_stay_bounded_and_keep_the_error_flag()
    {
        using var repo = TempRepo.Create(("src/A.cs", "public class A { public void Run() { } }"));
        await using var h = await ServerHarness.StartAsync(repo.Root);
        var cases = new (string Tool, Dictionary<string, object?> Args, string Reason)[]
        {
            ("find_symbol", new() { ["query"] = "A", ["project"] = new string('p', 5000), ["max_tokens"] = 256 }, "Unknown project"),
            ("search_code", new() { ["pattern"] = new string('(', 5000), ["max_tokens"] = 256 }, "Invalid regex"),
            ("overview", new() { ["scope"] = new string('s', 5000), ["max_tokens"] = 256 }, "Unknown scope"),
            ("outline", new() { ["paths"] = new[] { "src/" + new string('n', 5000) + ".cs" }, ["max_tokens"] = 256 }, "not found"),
            ("read_symbol", new() { ["symbol"] = new string('q', 5000), ["max_tokens"] = 256 }, "No symbol"),
        };
        foreach (var (tool, args, reason) in cases)
        {
            var r = await h.Client.CallToolAsync(tool, args);
            Assert.True(r.IsError == true, tool);
            Assert.Contains(reason, r.Text());
            Assert.True(OutputWriter.EstimateTokens(r.Text()) <= Budget.Minimum, $"{tool}: {OutputWriter.EstimateTokens(r.Text())} tokens");
        }
    }

    [Fact]
    public async Task Bad_input_returns_an_error_result()
    {
        using var repo = TempRepo.Create();
        await using var h = await ServerHarness.StartAsync(repo.Root);
        var r = await h.Client.CallToolAsync("outline", new Dictionary<string, object?> { ["paths"] = new[] { "../x.cs" } });
        Assert.True(r.IsError == true);
        Assert.Contains("Path is outside the repository root: ../x.cs", r.Text());
    }
}
