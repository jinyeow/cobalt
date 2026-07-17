using System.Net;
using System.Text;
using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Tui.App;
using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// CACHE-1: the PR/WI list screens are built once and kept alive across section switches
/// (toggle Add/Remove, not Dispose+rebuild), so switching away and back does not refetch the list.
/// </summary>
public class ShellKeepAliveTests
{
    private static readonly IApplication App = Application.Create();

    private static readonly AdoContext Context = new()
    {
        Name = "work",
        OrganizationUrl = new Uri("https://dev.azure.com/contoso"),
        Project = "Proj",
    };

    /// <summary>Counts PR-list GETs and always returns an empty list.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            Interlocked.Increment(ref _calls);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":[]}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static PullRequestStoreAdapter Adapter(CountingHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        var api = new GitApi(new AdoHttp(httpClient), Context);
        return new PullRequestStoreAdapter(api, _ => Task.FromResult(Guid.Empty), project: Context.Project);
    }

    private static ShellViewModel Vm() => new(["work"], "work", PrScope.Org, ThemeChoice.Dark);

    [Fact]
    public void Switching_Away_And_Back_Reuses_The_Pr_Screen_Without_Rebuilding()
    {
        var handler = new CountingHandler();
        var vm = Vm();
        using var shell = new CobaltShell(App, vm, pullRequests: Adapter(handler));

        vm.HandleCommand(AppCommand.SectionPullRequests); // builds + loads the PR list once
        var first = shell.PrListScreen;
        Assert.NotNull(first);

        vm.HandleCommand(AppCommand.SectionWorkItems);     // hide (no WI adapter → placeholder)
        vm.HandleCommand(AppCommand.SectionPullRequests);  // show again

        // Same instance ⇒ ShowSection did not dispose+rebuild ⇒ Load (the only fetch trigger) never
        // ran a second time, so the rows are reused instead of refetched.
        Assert.Same(first, shell.PrListScreen);
    }
}
