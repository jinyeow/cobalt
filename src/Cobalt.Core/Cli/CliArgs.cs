namespace Cobalt.Core.Cli;

public enum CliCommand
{
    Tui,
    AuthLogin,
    AuthStatus,
    Version,
    Help,
}

public sealed record CliArgs
{
    public CliCommand Command { get; init; } = CliCommand.Tui;
    public string? Context { get; init; }
    public string? Error { get; init; }

    public static CliArgs Parse(IReadOnlyList<string> args)
    {
        var command = CliCommand.Tui;
        string? context = null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--version" or "-v":
                    return new CliArgs { Command = CliCommand.Version };
                case "--help" or "-h":
                    return new CliArgs { Command = CliCommand.Help };
                case "--context":
                    if (i + 1 >= args.Count)
                    {
                        return Fail("--context requires a value (a context name from your config).");
                    }
                    context = args[++i];
                    break;
                case "auth":
                    if (i + 1 >= args.Count || args[i + 1] is not ("login" or "status"))
                    {
                        return Fail("usage: cobalt auth <login|status>");
                    }
                    command = args[++i] == "login" ? CliCommand.AuthLogin : CliCommand.AuthStatus;
                    break;
                default:
                    return Fail($"unknown argument '{args[i]}' (try --help)");
            }
        }

        return new CliArgs { Command = command, Context = context };

        static CliArgs Fail(string message) => new() { Command = CliCommand.Help, Error = message };
    }
}
