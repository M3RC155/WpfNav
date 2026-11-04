using System.Globalization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using WpfNav.Core;
using WpfNav.Core.Output;

namespace WpfNav.Server.Tools;

/// <summary>
/// The tool boundary: user errors become MCP error results with their message; anything else is logged first. Every text
/// WpfNav returns from here, result or error, stays within the request's budget (or the tool's ceiling when the requested
/// budget itself is invalid).
/// </summary>
internal static class ToolRunner
{
    public static async Task<string> RunAsync(ILogger log, string tool, int? maxTokens, int ceiling, Func<Task<string>> body)
    {
        var bound = Budget.Bound(maxTokens, ceiling);
        string text;
        try
        {
            text = await body();
        }
        catch (WpfNavException ex)
        {
            throw new McpException(Budget.Fit(ex.Message, bound));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "wpfnav {Tool} failed", tool);
            throw new McpException(Budget.Fit($"wpfnav internal error in {tool}: {ex.GetType().Name}: {ex.Message}", bound));
        }

        var tokens = OutputWriter.EstimateTokens(text);
        if (tokens <= bound) return text;
        // A renderer broke its budget. Cutting the text could leave a continuation that skips content, so withhold it.
        var estimate = tokens.ToString("N0", CultureInfo.InvariantCulture);
        log.LogError("wpfnav {Tool} produced {Tokens} estimated tokens, over its max_tokens={Budget}; the result was withheld", tool, estimate, bound);
        throw new McpException(Budget.Fit(
            $"wpfnav internal error in {tool}: the result exceeded max_tokens={bound} (estimated {estimate} tokens) and was withheld; retry with narrower arguments.",
            bound));
    }

    public static Task<string> Run(ILogger log, string tool, int? maxTokens, int ceiling, Func<string> body) =>
        RunAsync(log, tool, maxTokens, ceiling, () => Task.FromResult(body()));
}
