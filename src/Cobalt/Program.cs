using System.Reflection;
using Cobalt.Cli;
using Cobalt.Core.Cli;
using Cobalt.Core.Config;

var parsed = CliArgs.Parse(args);

if (parsed.Error is not null)
{
    Console.Error.WriteLine($"cobalt: {parsed.Error}");
    return 2;
}

try
{
    switch (parsed.Command)
    {
        case CliCommand.Version:
            Console.WriteLine(InformationalVersion());
            return 0;
        case CliCommand.Help:
            PrintHelp();
            return 0;
        case CliCommand.AuthLogin:
            return await AuthCommands.LoginAsync(LoadConfig());
        case CliCommand.AuthStatus:
            return await AuthCommands.StatusAsync(LoadConfig());
        case CliCommand.Tui:
        default:
            _ = LoadConfig().Resolve(parsed.Context); // validate before the TUI exists
            Console.Error.WriteLine("cobalt: the TUI arrives in milestone M2.");
            return 1;
    }
}
catch (ConfigException ex)
{
    Console.Error.WriteLine($"cobalt: {ex.Message}");
    return 2;
}

static CobaltConfig LoadConfig() => ConfigLoader.Load(ConfigPaths.ConfigFile());

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
