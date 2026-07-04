using Cobalt.Core.Ado;
using Cobalt.Core.Auth;
using Cobalt.Core.Config;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;

namespace Cobalt.Tui.App;

public static class CobaltTuiApp
{
    public static int Run(CobaltConfig config, string? contextOverride, ITokenProvider tokens)
    {
        var context = config.Resolve(contextOverride);
        var vm = new ShellViewModel([.. config.Contexts.Keys.Order(StringComparer.Ordinal)], context.Name);

        using var connection = AdoConnection.Create(context, tokens);
        var workItems = new WorkItemStoreAdapter(new WorkItemsApi(connection.Http, context));

        using var app = Application.Create().Init();
        var shell = new CobaltShell(app, vm, workItems);

        ResolveIdentityInBackground(app, vm, connection);

        app.Run(shell);
        shell.Dispose();
        return 0;
    }

    /// <summary>Fills the status bar with who we are; failures land in the message bar, never block startup.</summary>
    private static void ResolveIdentityInBackground(
        IApplication app, ShellViewModel vm, AdoConnection connection)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var user = await connection.Identity.GetAuthenticatedUserAsync().ConfigureAwait(false);
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
