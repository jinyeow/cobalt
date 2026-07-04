using Cobalt.Core.Ado;
using Cobalt.Core.Auth;
using Cobalt.Core.Config;

namespace Cobalt.Cli;

/// <summary>`cobalt auth login` / `cobalt auth status` — composition-root glue, no logic.</summary>
internal static class AuthCommands
{
    private static string AuthRecordPath => Path.Combine(ConfigPaths.ConfigDirectory(), "auth-record.json");

    internal static async Task<int> LoginAsync(CobaltConfig config)
    {
        Console.WriteLine("Opening a browser for Entra ID sign-in…");
        var record = await AzureTokenProvider.LoginAsync(AuthRecordPath);
        Console.WriteLine($"Signed in as {record.Username}.");
        return await StatusAsync(config);
    }

    internal static async Task<int> StatusAsync(CobaltConfig config)
    {
        var tokens = AzureTokenProvider.CreateDefault(AuthRecordPath);
        var failures = 0;

        // Contexts often share an org; ask each org who we are only once.
        var byOrg = config.Contexts.Values.GroupBy(c => c.OrganizationUrl);
        foreach (var org in byOrg)
        {
            string status;
            try
            {
                using var connection = AdoConnection.Create(org.First(), tokens);
                var user = await connection.Identity.GetAuthenticatedUserAsync();
                status = $"signed in as {user.DisplayName} ({user.Id})";
            }
            catch (Exception ex) when (ex is AdoApiException or Azure.Identity.CredentialUnavailableException
                or Azure.Identity.AuthenticationFailedException or HttpRequestException)
            {
                failures++;
                status = $"NOT signed in — {FirstLine(ex.Message)}";
            }

            foreach (var context in org)
            {
                var marker = context.Name == config.DefaultContext ? "*" : " ";
                Console.WriteLine($"{marker} {context.Name,-12} {org.Key} [{context.Project}]");
            }
            Console.WriteLine($"    {status}");
        }

        return failures == 0 ? 0 : 1;
    }

    private static string FirstLine(string message)
    {
        var newline = message.IndexOf('\n');
        return newline < 0 ? message : message[..newline].TrimEnd('\r');
    }
}
