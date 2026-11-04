using ModelContextProtocol;
using WpfNav.Server.Tools;

namespace WpfNav.Tests.Server;

public class ToolRunnerTests
{
    sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task An_oversized_success_becomes_a_bounded_internal_error_and_a_diagnostic()
    {
        var log = new ListLogger();
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            ToolRunner.RunAsync(log, "outline", 300, Limits.Outline, () => Task.FromResult(new string('x', 5000))));
        Assert.StartsWith("wpfnav internal error in outline: the result exceeded max_tokens=300", ex.Message);
        Assert.True(OutputWriter.EstimateTokens(ex.Message) <= 300);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("outline") && e.Message.Contains("1,429"));
    }

    [Fact]
    public async Task Expected_errors_are_abbreviated_to_the_budget_keeping_reason_and_remedy()
    {
        var message = "Unknown thing '" + new string('t', 5000) + "'. Pass a project name or a folder.";
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            ToolRunner.RunAsync(new ListLogger(), "overview", 256, Limits.Overview, () => throw new WpfNavException(message)));
        Assert.True(OutputWriter.EstimateTokens(ex.Message) <= 256);
        Assert.StartsWith("Unknown thing 'ttt", ex.Message);
        Assert.EndsWith("'. Pass a project name or a folder.", ex.Message);
    }

    [Fact]
    public async Task Invalid_budgets_bound_their_errors_by_the_default_ceiling()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            ToolRunner.RunAsync(new ListLogger(), "find_symbol", 10, Limits.FindSymbol, () => throw new WpfNavException(new string('e', 20000))));
        Assert.True(OutputWriter.EstimateTokens(ex.Message) <= Limits.FindSymbol);
        Assert.True(OutputWriter.EstimateTokens(ex.Message) > 256);
    }

    [Fact]
    public async Task Unexpected_failures_are_logged_and_bounded_and_cancellation_passes_through()
    {
        var log = new ListLogger();
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            ToolRunner.RunAsync(log, "search_code", 256, Limits.SearchCode, () => throw new InvalidOperationException(new string('z', 9000))));
        Assert.StartsWith("wpfnav internal error in search_code: InvalidOperationException: zzz", ex.Message);
        Assert.True(OutputWriter.EstimateTokens(ex.Message) <= 256);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ToolRunner.RunAsync(log, "search_code", null, Limits.SearchCode, () => throw new OperationCanceledException()));
    }

    [Fact]
    public async Task A_result_within_the_budget_is_returned_unchanged() =>
        Assert.Equal("ok\n", await ToolRunner.RunAsync(new ListLogger(), "overview", null, Limits.Overview, () => Task.FromResult("ok\n")));
}
