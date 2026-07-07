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
        {
            var config = LoadConfig();
            _ = config.Resolve(parsed.Context); // fail fast on a bad --context
            var tokens = Cobalt.Core.Auth.AzureTokenProvider.CreateDefault(
                Path.Join(ConfigPaths.ConfigDirectory(), "auth-record.json"));
            return Cobalt.Tui.App.CobaltTuiApp.Run(config, parsed.Context, tokens);
        }
    }
}
catch (ConfigException ex)
{
    Console.Error.WriteLine($"cobalt: {ex.Message}");
    return 2;
}
catch (Exception ex) when (ex is Azure.Identity.AuthenticationFailedException or OperationCanceledException)
{
    // Login cancelled, browser closed, conditional access, timeout — user-facing, not a crash.
    Console.Error.WriteLine($"cobalt: sign-in failed — {FirstLine(ex.Message)}");
    return 1;
}
catch (IOException ex)
{
    Console.Error.WriteLine($"cobalt: {ex.Message}");
    return 1;
}

static string FirstLine(string message)
{
    var newline = message.IndexOf('\n');
    return newline < 0 ? message : message[..newline].TrimEnd('\r');
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
