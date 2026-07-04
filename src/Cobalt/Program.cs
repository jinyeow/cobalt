using System.Reflection;
using Cobalt.Core.Cli;

var parsed = CliArgs.Parse(args);

if (parsed.Error is not null)
{
    Console.Error.WriteLine($"cobalt: {parsed.Error}");
    return 2;
}

switch (parsed.Command)
{
    case CliCommand.Version:
        Console.WriteLine(InformationalVersion());
        return 0;
    case CliCommand.Help:
        PrintHelp();
        return 0;
    case CliCommand.AuthLogin:
    case CliCommand.AuthStatus:
        Console.Error.WriteLine("cobalt: auth commands arrive in milestone M1.");
        return 1;
    case CliCommand.Tui:
    default:
        Console.Error.WriteLine("cobalt: the TUI arrives in milestone M2.");
        return 1;
}

static string InformationalVersion()
{
    var attr = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
    var version = attr?.InformationalVersion ?? "unknown";
    // Strip the source-revision suffix SourceLink appends after '+'.
    var plus = version.IndexOf('+');
    return plus >= 0 ? version[..plus] : version;
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        cobalt — a vim-flavored terminal UI for Azure DevOps

        usage:
          cobalt [--context <name>]     launch the TUI
          cobalt auth login             sign in (Entra ID)
          cobalt auth status            show who you are in each context
          cobalt --version              print version
          cobalt --help                 this help
        """);
}
