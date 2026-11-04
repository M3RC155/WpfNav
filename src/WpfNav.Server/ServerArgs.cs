using Microsoft.Extensions.Logging;
using WpfNav.Core;

namespace WpfNav.Server;

public sealed record ServerArgs(string Root, IReadOnlyList<string> Excludes, LogLevel LogLevel)
{
    public const string Usage =
        "Usage: WpfNav.Server [--root <repo>] [--exclude <glob>]... [--log-level <Trace|Debug|Information|Warning|Error>]";

    public static ServerArgs Parse(string[] args)
    {
        var root = Environment.CurrentDirectory;
        var excludes = new List<string>();
        var level = LogLevel.Information;
        for (var i = 0; i < args.Length; i++)
        {
            var flag = args[i];
            if (flag is not ("--root" or "--exclude" or "--log-level"))
                throw new WpfNavException($"Unknown argument: {flag}. {Usage}");
            if (i + 1 >= args.Length) throw new WpfNavException($"Missing value for {flag}. {Usage}");
            var value = args[++i];
            switch (flag)
            {
                case "--root":
                    root = value;
                    break;
                case "--exclude":
                    excludes.Add(value);
                    break;
                default:
                    if (!Enum.TryParse(value, ignoreCase: true, out level) || !Enum.IsDefined(level))
                        throw new WpfNavException($"Unknown log level: {value}. {Usage}");
                    break;
            }
        }
        return new ServerArgs(root, excludes, level);
    }
}
