using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WpfNav.Core;
using WpfNav.Core.Navigation;
using WpfNav.Core.Output;

namespace WpfNav.Server.Tools;

[McpServerToolType]
public sealed class OutlineTool(Workspace ws, ILogger<OutlineTool> log)
{
    [McpServerTool(Name = "outline", ReadOnly = true), Description(ToolText.Outline)]
    public Task<string> Outline(
        [Description("Files or folders, repo-relative or absolute. No globs.")] string[] paths,
        [Description("types or members. Default: members for files, types for folders.")] string? level = null,
        [Description("Resolved files to skip, from a previous continuation note.")] int offset = 0,
        [Description("Outline entries to skip in the first remaining file, from a previous continuation note.")] int entry_offset = 0,
        [Description(ToolText.MaxTokensOutline)] int? max_tokens = null) =>
        ToolRunner.Run(log, "outline", max_tokens, Limits.Outline, () => OutlineBuilder.Build(ws, paths, level, offset, entry_offset, max_tokens));
}
