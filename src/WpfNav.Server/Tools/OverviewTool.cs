using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WpfNav.Core;
using WpfNav.Core.Navigation;
using WpfNav.Core.Output;

namespace WpfNav.Server.Tools;

[McpServerToolType]
public sealed class OverviewTool(Workspace ws, ILogger<OverviewTool> log)
{
    [McpServerTool(Name = "overview", ReadOnly = true), Description(ToolText.Overview)]
    public Task<string> Overview(
        [Description("Project name or folder (repo-relative or absolute). Omit for the whole repo.")] string? scope = null,
        [Description(ToolText.MaxTokensOverview)] int? max_tokens = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync(log, "overview", max_tokens, Limits.Overview, () => OverviewBuilder.BuildAsync(ws, scope, max_tokens, ct));
}
