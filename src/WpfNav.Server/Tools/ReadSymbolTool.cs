using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WpfNav.Core;
using WpfNav.Core.Navigation;
using WpfNav.Core.Output;

namespace WpfNav.Server.Tools;

[McpServerToolType]
public sealed class ReadSymbolTool(Workspace ws, ILogger<ReadSymbolTool> log)
{
    [McpServerTool(Name = "read_symbol", ReadOnly = true), Description(ToolText.ReadSymbol)]
    public Task<string> ReadSymbol(
        [Description("Name, Type.Member, Namespace.Type.Member, or path:line.")] string symbol,
        [Description("File to search instead of the whole repo.")] string? path = null,
        [Description("Zero-based source line to start at, counted from the declaration's first line (doc comments and attributes included). Turns on paging; take it from a continuation note.")] int? line_offset = null,
        [Description("Most source lines in this page. Turns on paging.")] int? line_limit = null,
        [Description("Zero-based UTF-16 column within the line at line_offset, for continuing one very long line. Needs line_offset.")] int column_offset = 0,
        [Description(ToolText.MaxTokensReadSymbol)] int? max_tokens = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync(log, "read_symbol", max_tokens, Limits.ReadSymbol,
            () => SymbolReader.ReadAsync(ws, symbol, path, new ReadOptions(line_offset, line_limit, column_offset, max_tokens), ct));
}
