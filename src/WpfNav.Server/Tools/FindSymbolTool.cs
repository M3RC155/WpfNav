using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WpfNav.Core;
using WpfNav.Core.Index;
using WpfNav.Core.Navigation;
using WpfNav.Core.Output;

namespace WpfNav.Server.Tools;

[McpServerToolType]
public sealed class FindSymbolTool(Workspace ws, ILogger<FindSymbolTool> log)
{
    [McpServerTool(Name = "find_symbol", ReadOnly = true), Description(ToolText.FindSymbol)]
    public Task<string> FindSymbol(
        [Description("Name, partial name, Type.Member, * wildcard, or camelCase initials (OEVM).")] string query,
        [Description("type, class, interface, struct, record, enum, delegate, member, method, ctor, property, field, event, resource (XAML x:Key) or element (XAML x:Name).")] string? kind = null,
        [Description("Project name.")] string? project = null,
        [Description("Folder or file prefix.")] string? path = null,
        [Description("Base type name; returns types deriving from it directly or indirectly (matched by name).")] string? derives_from = null,
        [Description("Maximum matches (default 20).")] int limit = 20,
        [Description("Matches to skip, from a previous '… more' note.")] int offset = 0,
        [Description(ToolText.MaxTokensFindSymbol)] int? max_tokens = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync(log, "find_symbol", max_tokens, Limits.FindSymbol,
            () => SymbolFinder.FindAsync(ws, new SymbolQuery(query, kind, project, path, derives_from), limit, offset, max_tokens, ct));
}
