using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace WpfNav.Tests.TestSupport;

/// <summary>The real server wiring, hosted in-process and connected to an MCP client over in-memory pipes.</summary>
public sealed class ServerHarness : IAsyncDisposable
{
    readonly IHost _host;

    ServerHarness(IHost host, McpClient client, Workspace workspace)
    {
        _host = host;
        Client = client;
        Workspace = workspace;
    }

    public McpClient Client { get; }

    public Workspace Workspace { get; }

    public static async Task<ServerHarness> StartAsync(string root)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var workspace = new Workspace(new RepoRoot(root), []);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddWpfNav(workspace)
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        var host = builder.Build();
        await host.StartAsync();
        var client = await McpClient.CreateAsync(new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));
        return new ServerHarness(host, client, workspace);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        Workspace.Dispose();
    }
}
