using System.Net;
using System.Text;
using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Tui.App;
using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Input;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// The shell's `?` is an executable menu (#20), not a static cheatsheet: it offers the active
/// scope's own rows and runs the chosen one after the popup closes. The popup itself needs a run
/// loop, so the pick is taken through <c>HelpMenuOverride</c> — the <c>ShowLogOverride</c> pattern.
/// </summary>
public class ShellHelpMenuTests
{
    private static readonly IApplication App = Application.Create();

    private static readonly AdoContext Context = new()
    {
        Name = "work",
        OrganizationUrl = new Uri("https://dev.azure.com/contoso"),
        Project = "Proj",
    };

    /// <summary>Answers every PR-list GET with an empty collection so a load settles to zero rows.</summary>
    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":[]}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static PullRequestStoreAdapter PrAdapter()
    {
        var httpClient = new HttpClient(new EmptyHandler()) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        return new PullRequestStoreAdapter(
            new GitApi(new AdoHttp(httpClient), Context), _ => Task.FromResult(Guid.Empty), project: Context.Project);
    }

    [Fact]
    public void Question_Mark_Offers_The_Active_Scopes_Rows()
    {
        var vm = new ShellViewModel(["work"], "work");
        using var shell = new CobaltShell(App, vm);
        IReadOnlyList<MenuOption<AppCommand>>? offered = null;
        shell.HelpMenuOverride = rows => { offered = rows; return null; };
        shell.SetFocus();

        shell.NewKeyDownEvent(new Key('?'));

        Assert.NotNull(offered);
        Assert.Equal(
            HelpText.MenuFor(KeyBindingTable.Shared, shell.ActiveScope, previewVisible: false),
            offered);
    }

    [Fact]
    public void A_Chosen_Row_Is_Dispatched_After_The_Menu_Closes()
    {
        var vm = new ShellViewModel(["work"], "work");
        using var shell = new CobaltShell(App, vm, pullRequests: PrAdapter());
        vm.HandleCommand(AppCommand.SectionPullRequests); // the section with an observable reload seam
        var loadsBefore = shell.PrListScreen!.LoadCount;
        shell.HelpMenuOverride = _ => AppCommand.Refresh;
        shell.SetFocus();

        shell.NewKeyDownEvent(new Key('?'));

        Assert.True(
            shell.PrListScreen!.LoadCount > loadsBefore,
            $"expected the chosen Refresh row to reload the list, {loadsBefore} → {shell.PrListScreen!.LoadCount}");
    }

    [Fact]
    public void Dismissing_The_Menu_Dispatches_Nothing()
    {
        var vm = new ShellViewModel(["work"], "work");
        using var shell = new CobaltShell(App, vm, pullRequests: PrAdapter());
        vm.HandleCommand(AppCommand.SectionPullRequests);
        var loadsBefore = shell.PrListScreen!.LoadCount;
        shell.HelpMenuOverride = _ => null;
        shell.SetFocus();

        shell.NewKeyDownEvent(new Key('?'));

        Assert.Equal(loadsBefore, shell.PrListScreen!.LoadCount);
        Assert.DoesNotContain(vm.Messages.History, m => m.Text.Contains("not available here"));
    }

    [Fact]
    public void The_Help_Palette_Command_Opens_The_Same_Menu()
    {
        var vm = new ShellViewModel(["work"], "work");
        using var shell = new CobaltShell(App, vm);
        var opened = 0;
        shell.HelpMenuOverride = _ => { opened++; return null; };

        vm.HandlePaletteInput("help"); // parser -> HelpRequested -> the shell's ShowHelp

        Assert.Equal(1, opened);
    }
}
