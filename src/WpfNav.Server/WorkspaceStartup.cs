using Microsoft.Extensions.Hosting;
using WpfNav.Core;

namespace WpfNav.Server;

/// <summary>Starts indexing when the host starts, without delaying the MCP handshake.</summary>
public sealed class WorkspaceStartup(Workspace ws) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ws.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
