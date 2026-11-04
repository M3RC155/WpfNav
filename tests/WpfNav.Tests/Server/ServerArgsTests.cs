namespace WpfNav.Tests.Server;

public class ServerArgsTests
{
    [Fact]
    public void Parses_every_flag()
    {
        var a = ServerArgs.Parse(new[] { "--root", @"C:\r", "--exclude", "a/**", "--exclude", "b/**", "--log-level", "Debug" });
        Assert.Equal(@"C:\r", a.Root);
        Assert.Equal(new[] { "a/**", "b/**" }, a.Excludes);
        Assert.Equal(LogLevel.Debug, a.LogLevel);
    }

    [Fact]
    public void Defaults_to_the_working_directory_and_information_logging()
    {
        var a = ServerArgs.Parse(Array.Empty<string>());
        Assert.Equal((Environment.CurrentDirectory, 0, LogLevel.Information), (a.Root, a.Excludes.Count, a.LogLevel));
    }

    [Theory]
    [InlineData("--bogus", "Unknown argument: --bogus. " + ServerArgs.Usage)]
    [InlineData("--root", "Missing value for --root. " + ServerArgs.Usage)]
    public void Bad_arguments_explain_usage(string arg, string message) =>
        Assert.Equal(message, Assert.Throws<WpfNavException>(() => ServerArgs.Parse(new[] { arg })).Message);
}
