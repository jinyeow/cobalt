using Cobalt.Core.Ado;
using Cobalt.Core.Auth;
using Cobalt.Core.Config;
using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;

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
        catch (Exception ex)
        {
            // Last-resort crash boundary (ADR 0013). RunCore's `using var app` has already
            // disposed Terminal.Gui and restored the terminal as this exception unwound, so
            // writing to stderr here lands on a clean terminal instead of the alternate screen.
            return HandleCrash(ex, ConfigPaths.CrashLogFile(), DateTimeOffset.Now, Console.Error);
        }
    }

    private static int RunCore(CobaltConfig config, string? contextOverride, ITokenProvider tokens)
    {
        var context = config.Resolve(contextOverride);
        var vm = new ShellViewModel(
            [.. config.Contexts.Keys.Order(StringComparer.Ordinal)], context.Name, context.PrScope);

        using var connection = AdoConnection.Create(context, tokens);
        var workItems = new WorkItemStoreAdapter(new WorkItemsApi(connection.Http, context), context.PrScope);

        // Resolve the signed-in user once and share it: the status bar and the PR
        // reviewer/creator filters both consume this single cached call.
        var identity = new Lazy<Task<AdoUser>>(() => connection.Identity.GetAuthenticatedUserAsync());
        var pullRequests = new PullRequestStoreAdapter(
            new GitApi(connection.Http, context),
            async ct => (await identity.Value.WaitAsync(ct).ConfigureAwait(false)).Id,
            context.PrScope);

        using var app = Application.Create().Init();
        // Lower input latency: the default 25 iterations/sec adds up to ~40ms per
        // keystroke; 60 halves that to ~16ms for a snappier vim feel.
        Application.MaximumIterationsPerSecond = 60;
        using var shell = new CobaltShell(app, vm, workItems, pullRequests, editor: null, context: context);

        ResolveIdentityInBackground(app, vm, identity);

        app.Run(shell);
        return 0;
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
