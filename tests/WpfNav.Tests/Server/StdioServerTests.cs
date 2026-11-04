namespace WpfNav.Tests.Server;

public class StdioServerTests
{
    [Fact]
    public async Task Real_exe_speaks_clean_json_rpc_on_stdout()
    {
        using var repo = TempRepo.Create(("src/A.cs", "class A { }"));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            { Name = "wpfnav", Command = TestPaths.ServerExe, Arguments = ["--root", repo.Root] }));
        Assert.Contains(await client.ListToolsAsync(), t => t.Name == "outline");
    }

    [Fact]
    public void Invalid_root_exits_with_code_2_and_says_why_on_stderr()
    {
        var p = Process.Start(new ProcessStartInfo(TestPaths.ServerExe)
            { ArgumentList = { "--root", @"C:\definitely\missing" }, RedirectStandardError = true, RedirectStandardOutput = true })!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
        Assert.Equal(2, p.ExitCode);
        Assert.Contains(@"wpfnav: Repository root not found: C:\definitely\missing", err);
    }
}
