using Cobalt.Core.Ado;
using Cobalt.Core.Auth;
using Cobalt.Core.Config;
using Cobalt.Core.Models;
using Cobalt.Tui.Theming;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;

namespace Cobalt.Tui.App;

public static class CobaltTuiApp
{
    private static bool _handlersInstalled;

    public static int Run(CobaltConfig config, string? contextOverride, ITokenProvider tokens)
    {
        InstallGlobalHandlers();
        try
        {
            return RunCore(config, contextOverride, tokens);
        }
        catch (Exception ex) when (ex is not ConfigException)
        {
            // Last-resort crash boundary (ADR 0013). RunCore's `using var app` has already
            // disposed Terminal.Gui and restored the terminal as this exception unwound, so
            // writing to stderr here lands on a clean terminal instead of the alternate screen.
            // ConfigException (e.g. a bad COBALT_DRIVER) is a user error, not a crash — it
            // propagates to Program.cs, which prints it cleanly and exits 2.
            return HandleCrash(ex, ConfigPaths.CrashLogFile(), DateTimeOffset.Now, Console.Error);
        }
    }

    private static int RunCore(CobaltConfig config, string? contextOverride, ITokenProvider tokens)
    {
        var context = config.Resolve(contextOverride);
        var vm = new ShellViewModel(
            [.. config.Contexts.Keys.Order(StringComparer.Ordinal)], context.Name, context.PrScope, config.Theme);

        using var connection = AdoConnection.Create(context, tokens);

        // Open the connection while the UI builds, so the first real call skips the ~700ms cold
        // DNS + TCP + TLS. Dropped on the floor deliberately rather than routed through
        // FireAndForget: WarmUpAsync is silent by contract, and FireAndForget would report a
        // warm-up fault to the message bar and the crash log — the two things it must never do.
        _ = connection.Http.WarmUpAsync();

        var workItems = new WorkItemStoreAdapter(new WorkItemsApi(connection.Http, context), context.PrScope);

        // Resolve the signed-in user once and share it: the status bar and the PR
        // reviewer/creator filters both consume this single cached call.
        var identity = new Lazy<Task<AdoUser>>(() => connection.Identity.GetAuthenticatedUserAsync());
        var pullRequests = new PullRequestStoreAdapter(
            new GitApi(connection.Http, context),
            async ct => (await identity.Value.WaitAsync(ct).ConfigureAwait(false)).Id,
            ct => BuildTeamDirectoryAsync(connection.Teams, ct),
            context.Project,
            context.PrScope,
            new PolicyApi(connection.Http));

        var driverName = ResolveDriver(Environment.GetEnvironmentVariable, DriverRegistry.GetDriverNames().ToArray());
        // Enable Terminal.Gui's theming (scoped to its embedded config) before Init so the driver
        // starts on the resolved theme; the initial preset is applied just below.
        ThemeService.Enable();
        using var app = Application.Create().Init(driverName);
        // Lower input latency: the default 25 iterations/sec adds up to ~40ms per
        // keystroke; 60 halves that to ~16ms for a snappier vim feel.
        Application.MaximumIterationsPerSecond = 60;
        // Declared before the shell so disposal runs shell→monitor: the shell unsubscribes its
        // Changed handler before the monitor (whose watcher thread raises it) is disposed.
        using var monitor = OsThemeMonitor.Create();
        ThemeService.Apply(ThemeResolver.Resolve(config.Theme, monitor.Current));
        using var shell = new CobaltShell(
            app, vm, workItems, pullRequests, editor: null, context: context, themeMonitor: monitor);

        ResolveIdentityInBackground(app, vm, identity);

        app.Run(shell);
        return 0;
    }

    /// <summary>
    /// Resolves the Terminal.Gui driver. An explicit <c>COBALT_DRIVER</c> wins (matched
    /// case-insensitively against <paramref name="knownDrivers"/>, returned canonical; an
    /// unknown value throws an actionable <see cref="ConfigException"/>). Otherwise the
    /// <c>dotnet</c> driver is selected when a terminal multiplexer (<c>ZELLIJ</c>/<c>TMUX</c>)
    /// or a remote/RDP session (<c>SESSIONNAME=RDP-*</c>) is detected. Failing all of these,
    /// <see langword="null"/> lets TG auto-detect (<c>windows</c> on Windows).
    ///
    /// <para>The Win32-console <c>windows</c> driver is unreliable through a multiplexer's
    /// pseudo-terminal — it drops keystrokes and mishandles the editor suspend/resume — and
    /// under a remote session its console-buffer painting is translated to VT by ConPTY,
    /// which is expensive over a latency link on a GPU-less host. In both cases cobalt
    /// defaults to the stdio/ANSI <c>dotnet</c> driver, which writes VT straight to stdout.
    /// Set <c>COBALT_DRIVER</c> explicitly to override (e.g. <c>=windows</c> to force it back,
    /// or for an environment this detection misses). See ADR 0016.</para>
    /// </summary>
    internal static string? ResolveDriver(Func<string, string?> env, IReadOnlyCollection<string> knownDrivers)
    {
        var requested = env("COBALT_DRIVER")?.Trim();
        if (!string.IsNullOrEmpty(requested))
        {
            return knownDrivers.FirstOrDefault(d => d.Equals(requested, StringComparison.OrdinalIgnoreCase))
                ?? throw new ConfigException(
                    $"COBALT_DRIVER='{requested}' is not a known Terminal.Gui driver. " +
                    $"Valid drivers: {string.Join(", ", knownDrivers)}.");
        }

        var inMultiplexer = !string.IsNullOrEmpty(env("ZELLIJ")) || !string.IsNullOrEmpty(env("TMUX"));
        // A remote/RDP session (SESSIONNAME=RDP-Tcp#N, e.g. a Windows 365 Cloud PC) paints
        // through ConPTY's console-buffer→VT translation on the 'windows' driver — measurably
        // expensive over a latency link on a GPU-less host, where the terminal renders in
        // software. The stdio/ANSI 'dotnet' driver writes VT straight to stdout and skips it.
        var inRemoteSession = env("SESSIONNAME")?.StartsWith("RDP-", StringComparison.OrdinalIgnoreCase) == true;
        return inMultiplexer || inRemoteSession
            // FirstOrDefault, not First: if 'dotnet' is somehow unregistered, fall back to
            // TG's default (null) rather than throwing into the crash boundary.
            ? knownDrivers.FirstOrDefault(d => d.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>
    /// Resolves the user's team memberships for the Team tab: one <c>$mine=true</c> call
    /// for the teams, then one members call per team (in parallel). The result is cached
    /// by the adapter for its lifetime, so this runs at most once.
    /// </summary>
    private static async Task<TeamDirectory> BuildTeamDirectoryAsync(TeamsApi teams, CancellationToken ct)
    {
        var myTeams = await teams.GetMyTeamsAsync(ct).ConfigureAwait(false);
        var memberships = await Task.WhenAll(myTeams.Select(async team =>
        {
            var members = await teams.GetTeamMembersAsync(team.ProjectId, team.Id, ct).ConfigureAwait(false);
            IReadOnlySet<string> ids = members.Select(m => m.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new TeamMembership(team.Id, team.ProjectName, ids);
        })).ConfigureAwait(false);
        return new TeamDirectory(memberships);
    }

    /// <summary>
    /// Writes an escaping exception to the crash log, tells the user where, and returns
    /// a non-zero exit code. Testable in isolation: timestamp and sinks are injected.
    /// </summary>
    internal static int HandleCrash(Exception exception, string logPath, DateTimeOffset now, TextWriter stderr)
    {
        string reported;
        try
        {
            reported = CrashLog.Write(logPath, exception, now);
        }
        catch (Exception)
        {
            // Last-resort boundary: a failed crash-log write (permission denied →
            // UnauthorizedAccessException, malformed $XDG_STATE_HOME → Argument/NotSupported,
            // full disk → IOException) must never itself crash cobalt or hide the crash.
            // Point the user at the intended path and dump the stack to stderr as a fallback.
            stderr.WriteLine($"cobalt crashed — see {logPath}");
            stderr.WriteLine(exception);
            return 1;
        }
        stderr.WriteLine($"cobalt crashed — see {reported}");
        return 1;
    }

    /// <summary>Routes a fire-and-forget/background fault to the same crash log; logging is best-effort.</summary>
    internal static void LogBackgroundFault(Exception exception, string logPath, DateTimeOffset now)
    {
        try
        {
            CrashLog.Write(logPath, exception, now);
        }
        catch (Exception)
        {
            // Last-resort boundary: never crash the finalizer/background thread over a failed
            // log write, whatever it throws (permission, malformed path, full disk, …).
        }
    }

    private static void InstallGlobalHandlers()
    {
        if (_handlersInstalled)
        {
            return;
        }
        _handlersInstalled = true;

        var logPath = ConfigPaths.CrashLogFile();
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Observe first: a throw from the log write below must never terminate the
            // finalizer thread (which an unobserved exception on it would).
            e.SetObserved();
            LogBackgroundFault(e.Exception, logPath, DateTimeOffset.Now);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogBackgroundFault(ex, logPath, DateTimeOffset.Now);
                // The process is terminating; still tell the user where the log is.
                Console.Error.WriteLine($"cobalt crashed — see {logPath}");
            }
        };
    }

    /// <summary>Fills the status bar with who we are; failures land in the message bar, never block startup.</summary>
    private static void ResolveIdentityInBackground(
        IApplication app, ShellViewModel vm, Lazy<Task<AdoUser>> identity)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var user = await identity.Value.ConfigureAwait(false);
                app.Invoke(() => vm.OnUserResolved(user.DisplayName));
            }
            catch (Exception ex) when (ex is AdoApiException
                or Azure.Identity.AuthenticationFailedException
                or HttpRequestException
                or OperationCanceledException
                or System.Text.Json.JsonException)
            {
                var message = ex.Message;
                app.Invoke(() => vm.Messages.Error($"not signed in — {FirstLine(message)} (run: cobalt auth login)"));
            }
        });
    }

    private static string FirstLine(string message)
    {
        var newline = message.IndexOf('\n');
        return newline < 0 ? message : message[..newline].TrimEnd('\r');
    }
}
