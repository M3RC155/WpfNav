using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WpfNav.Core;
using WpfNav.Core.Output;
using WpfNav.Core.Search;

namespace WpfNav.Server.Tools;

[McpServerToolType]
public sealed class SearchCodeTool(Workspace ws, ILogger<SearchCodeTool> log)
{
    [McpServerTool(Name = "search_code", ReadOnly = true), Description(ToolText.SearchCode)]
    public Task<string> SearchCode(
        [Description(".NET regex, or plain text with literal=true. Matches within single lines.")] string pattern,
        [Description("Treat pattern as plain text.")] bool literal = false,
        [Description("Match whole words only.")] bool whole_word = false,
        [Description("Default true.")] bool case_sensitive = true,
        [Description("cs, xaml or all (default).")] string file_kind = "all",
        [Description("Project name.")] string? project = null,
        [Description("Folder or file prefix.")] string? path = null,
        [Description("Maximum groups (default 15).")] int limit = 15,
        [Description("Groups to skip, from a previous '… more' note.")] int offset = 0,
        [Description(ToolText.MaxTokensSearchCode)] int? max_tokens = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync(log, "search_code", max_tokens, Limits.SearchCode, () => CodeSearcher.SearchAsync(ws,
            new SearchRequest(pattern, literal, whole_word, case_sensitive, file_kind, project, path, limit, offset) { MaxTokens = max_tokens }, ct));
}
