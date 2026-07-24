using System.Runtime.InteropServices;
using Cobalt.Core.Ado;
using Cobalt.Core.Auth;
using Cobalt.Core.Config;
using Cobalt.Core.Models;
using Cobalt.Tui.Input;
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
        // Build the remapped key table up front: a bad [keys] section is a user config error, so
        // FromConfig throws ConfigException here (before the terminal is touched), which propagates
        // to Program.cs for a clean message rather than a mid-startup crash (ADR 0023).
        var bindings = KeyBindingTable.FromConfig(config.Keys);
        var vm = new ShellViewModel(
            [.. config.Contexts.Keys.Order(StringComparer.Ordinal)],
            context.Name,
            context.PrScope,
            config.Theme,
            config.Preview);

        using var connection = AdoConnection.Create(context, tokens);

        // Feed every ADO request into the :log view — wired BEFORE the prime below so the one
        // cached connectionData op the prime makes is deterministically logged (assigning the
        // observer after the prime would race the request and drop it). The observer fires on
        // threadpool continuation threads (AdoHttp's ConfigureAwait(false)); once the app exists it
        // marshals each record onto the UI thread, and until then (the prime can complete before
        // Init) it adds directly — OperationLog.Add is itself thread-safe, so that is race-free.
        IUiPost? postForLog = null;
        connection.Http.OperationObserver = OperationObserver(
            vm.Operations, run => { if (postForLog is { } p) { p.Post(run); } else { run(); } });

        // Prime the shared identity cache while the UI builds, so the first real call skips
        // the ~700ms cold DNS + TCP + TLS. Dropped on the floor deliberately rather than routed
        // through FireAndForget: priming is silent by contract, and FireAndForget would report
        // a warm-up fault to the message bar and the crash log — the two things it must never
        // do. Everything below reads the same cache through GetIdentityAsync, so cold start
        // makes one connectionData call, not a separate warm-up ping plus an identity read.
        _ = connection.PrimeIdentityAsync();

        var workItems = new WorkItemStoreAdapter(new WorkItemsApi(connection.Http, context), context.PrScope);

        var pullRequests = new PullRequestStoreAdapter(
            new GitApi(connection.Http, context),
            async ct => (await connection.GetIdentityAsync(ct).ConfigureAwait(false)).Id,
            ct => BuildTeamDirectoryAsync(connection.Teams, ct),
            context.Project,
            context.PrScope,
            new PolicyApi(connection.Http));

        var driverName = ResolveDriver(
            Environment.GetEnvironmentVariable,
            DriverRegistry.GetDriverNames().ToArray(),
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        // Detect the terminal's colour depth and publish it before the first draw (ADR 0019
        // extension), so the resolver below degrades the diff palette and Force16Colors degrades
        // the chrome in step. Same pure env seam as ResolveDriver — never probes the terminal.
        var caps = TerminalCapabilities.Detect(Environment.GetEnvironmentVariable);
        ThemeService.SetCapabilities(caps);
        // Enable Terminal.Gui's theming (scoped to its embedded config) before Init so the driver
        // starts on the resolved theme; the initial preset is applied just below.
        ThemeService.Enable();
        using var app = Application.Create().Init(driverName);
        // The one UI-thread marshalling seam (M2): everything that used to close over app.Invoke
        // now posts through this. Created right after Init so app.Invoke has a running main loop.
        var uiPost = new ApplicationUiPost(app);
        // Below truecolor, force TG's chrome down to the 16-colour path so it matches the degraded
        // diff palette (a truecolor terminal is byte-identical to before).
        if (caps.Color < ColorSupport.Full)
        {
            // Driver is guaranteed non-null immediately after Init (its getter throws otherwise).
            app.Driver!.Force16Colors = true;
        }
        // Lower input latency: the default 25 iterations/sec adds up to ~40ms per
        // keystroke; 60 halves that to ~16ms for a snappier vim feel.
        Application.MaximumIterationsPerSecond = 60;
        // The app now exists: subsequent :log records marshal onto its UI thread (see the observer
        // wiring above); anything the prime already reported was added directly and thread-safely.
        postForLog = uiPost;
        // Declared before the shell so disposal runs shell→monitor: the shell unsubscribes its
        // Changed handler before the monitor (whose watcher thread raises it) is disposed.
        using var monitor = OsThemeMonitor.Create();
        ThemeService.Apply(ThemeResolver.Resolve(config.Theme, monitor.Current, caps.Color));
        using var shell = new CobaltShell(
            app, vm, workItems, pullRequests, editor: null, context: context, themeMonitor: monitor,
            bindings: bindings, post: uiPost);

        ResolveIdentityInBackground(uiPost, vm, connection.GetIdentityAsync);

        app.Run(shell);
        return 0;
    }

    /// <summary>
    /// Builds the <c>AdoHttp.OperationObserver</c> that feeds the <c>:log</c> view: each recorded
    /// operation is pushed to <paramref name="log"/> through <paramref name="marshal"/>. Production
    /// passes <c>app.Invoke</c> so the threadpool-thread record lands on the UI thread before
    /// <see cref="OperationLog"/>'s subscribers run; a test passes a synchronous marshal to assert
    /// the record reaches the log. Split out as a pure seam because the real path needs a running
    /// application (<c>app.Invoke</c> requires <c>Init</c>).
    /// </summary>
    internal static Action<AdoOperation> OperationObserver(OperationLog log, Action<Action> marshal) =>
        operation => marshal(() => log.Add(operation));

    /// <summary>
    /// Resolves the Terminal.Gui driver. An explicit <c>COBALT_DRIVER</c> wins (matched
    /// case-insensitively against <paramref name="knownDrivers"/>, returned canonical; an
    /// unknown value throws an actionable <see cref="ConfigException"/>). Otherwise the
    /// <c>dotnet</c> driver is selected when a terminal multiplexer (<c>ZELLIJ</c>/<c>TMUX</c>)
    /// or a remote/RDP session (<c>SESSIONNAME=RDP-*</c>) is detected. Failing all of these,
    /// the platform default is pinned explicitly — <c>windows</c> on Windows, <c>dotnet</c>
    /// elsewhere — never Terminal.Gui's auto-detect: since 2.4.17 auto-detect selects the new
    /// <c>ansi</c> driver, whose input path drops every other keypress (vim <c>j</c>/<c>k</c>
    /// needs two presses per move; diagnosed 2026-07-22). <see langword="null"/> (TG picks) is
    /// only the last resort when even the pinned driver is unregistered.
    ///
    /// <para>The Win32-console <c>windows</c> driver is unreliable through a multiplexer's
    /// pseudo-terminal — it drops keystrokes and mishandles the editor suspend/resume — and
    /// under a remote session its console-buffer painting is translated to VT by ConPTY,
    /// which is expensive over a latency link on a GPU-less host. In both cases cobalt
    /// defaults to the stdio/ANSI <c>dotnet</c> driver, which writes VT straight to stdout.
    /// Set <c>COBALT_DRIVER</c> explicitly to override (e.g. <c>=windows</c> to force it back,
    /// or for an environment this detection misses). See ADR 0016.</para>
    /// </summary>
    internal static string? ResolveDriver(
        Func<string, string?> env, IReadOnlyCollection<string> knownDrivers, bool isWindows)
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
        if (inMultiplexer || inRemoteSession)
        {
            var dotnet = knownDrivers.FirstOrDefault(d => d.Equals("dotnet", StringComparison.OrdinalIgnoreCase));
            if (dotnet is not null)
            {
                return dotnet;
            }
            // 'dotnet' unregistered: fall through to the platform pin — deterministic beats
            // TG auto-detect (the broken 'ansi' driver).
        }

        // Pin the pre-2.4.17 platform default rather than returning null: null hands the
        // choice to TG auto-detect, which since 2.4.17 lands on the keypress-dropping 'ansi'
        // driver. FirstOrDefault, not First: with even this driver unregistered, null (TG
        // picks) is still better than throwing into the crash boundary.
        var preferred = isWindows ? "windows" : "dotnet";
        return knownDrivers.FirstOrDefault(d => d.Equals(preferred, StringComparison.OrdinalIgnoreCase));
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
        IUiPost post, ShellViewModel vm, Func<CancellationToken, Task<AdoUser>> getIdentity)
    {
        _ = Task.Run(async () =>
        {
            var outcome = await ResolveIdentityAsync(getIdentity).ConfigureAwait(false);
            post.Post(() =>
            {
                if (outcome.DisplayName is { } name)
                {
                    vm.OnUserResolved(name);
                }
                else
                {
                    vm.Messages.Error(outcome.ErrorMessage!);
                }
            });
        });
    }

    /// <summary>
    /// The pure half of the background identity resolve: expected auth/network faults (ADR 0013)
    /// become an actionable message rather than propagating. Split out from
    /// <see cref="ResolveIdentityInBackground"/> so it is testable without a running Terminal.Gui
    /// application (<c>IApplication.Invoke</c> requires <c>Init</c>).
    /// </summary>
    internal static async Task<IdentityOutcome> ResolveIdentityAsync(
        Func<CancellationToken, Task<AdoUser>> getIdentity, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await getIdentity(cancellationToken).ConfigureAwait(false);
            return new IdentityOutcome(user.DisplayName, null);
        }
        catch (Exception ex) when (ex is AdoApiException
            or Azure.Identity.AuthenticationFailedException
            or HttpRequestException
            or OperationCanceledException
            or System.Text.Json.JsonException)
        {
            return new IdentityOutcome(null, $"not signed in — {FirstLine(ex.Message)} (run: cobalt auth login)");
        }
    }

    /// <summary>Result of a background identity resolve: exactly one of the two members is set.</summary>
    internal readonly record struct IdentityOutcome(string? DisplayName, string? ErrorMessage);

    private static string FirstLine(string message)
    {
        var newline = message.IndexOf('\n');
        return newline < 0 ? message : message[..newline].TrimEnd('\r');
    }
}
