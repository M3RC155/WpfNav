using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WpfNav.Core;
using WpfNav.Core.Inventory;
using WpfNav.Server;

// stdout carries MCP JSON-RPC only: every diagnostic goes to stderr.
ServerArgs options;
RepoRoot root;
try
{
    options = ServerArgs.Parse(args);
    root = new RepoRoot(options.Root);
}
catch (WpfNavException ex)
{
    Console.Error.WriteLine($"wpfnav: {ex.Message}");
    return 2;
}

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(options.LogLevel);
builder.Services.AddWpfNav(new Workspace(root, options.Excludes)).WithStdioServerTransport();

var host = builder.Build();
host.Services.GetRequiredService<ILogger<Program>>().LogInformation("wpfnav serving {Root}", root.FullPath);
await host.RunAsync();
return 0;
