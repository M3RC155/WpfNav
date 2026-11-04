using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using WpfNav.Core;

namespace WpfNav.Server;

public static class ServerSetup
{
    public const string Instructions =
        "WpfNav navigates the C#/XAML repository at the root shown in each result's `root:` line. " +
        "Use `overview` when repository or project structure is unfamiliar. " +
        "Use `find_symbol` to discover or disambiguate declarations (partial names, `Type.Member`, camelCase initials). " +
        "Read a known target directly with `read_symbol` (name, `Type.Member` or `path:line`). " +
        "Use `outline` to inspect structure or choose members. Use `search_code` for grouped textual usages. " +
        "Results are size-capped: follow continuation positions (offset, entry_offset, line_offset, column_offset) " +
        "and select smaller max_tokens when less detail is sufficient. " +
        "Paths are relative to the root shown in results; line numbers are current.";

    /// <summary>Registers the workspace and the MCP server with its tools; the caller adds the transport.</summary>
    public static IMcpServerBuilder AddWpfNav(this IServiceCollection services, Workspace workspace)
    {
        services.AddSingleton(workspace);
        services.AddHostedService<WorkspaceStartup>();
        return services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "wpfnav", Version = "0.1.0" };
                o.ServerInstructions = Instructions;
            })
            .WithToolsFromAssembly(typeof(ServerSetup).Assembly);
    }
}
