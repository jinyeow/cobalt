using System.Net;
using System.Text;
using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Core.Models;
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

    private static ShellViewModel Vm(PrScope scope = PrScope.Org) => new(["work"], "work", scope, ThemeChoice.Dark);

    // Headless never draws, so a freshly built view is permanently NeedsDraw. Reset it via the
    // internal TG method so the INPUT-1 assertion can observe SetNeedsDraw flipping it back on.
    private static void ClearNeedsDraw(Terminal.Gui.ViewBase.View view) =>
        typeof(Terminal.Gui.ViewBase.View)
            .GetMethod("ClearNeedsDraw", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(view, null);

    [Fact]
    public void Switching_Away_And_Back_Reuses_The_Pr_Screen_And_Does_Not_Reload()
    {
        var vm = Vm();
        using var shell = new CobaltShell(App, vm, pullRequests: Adapter(new CountingHandler()));

        vm.HandleCommand(AppCommand.SectionPullRequests); // builds + loads the PR list once
        var first = shell.PrListScreen;
        Assert.NotNull(first);
        var loadsAfterFirstShow = first!.LoadCount;
        Assert.Equal(1, loadsAfterFirstShow);

        vm.HandleCommand(AppCommand.SectionWorkItems);     // hide (no WI adapter → placeholder)
        vm.HandleCommand(AppCommand.SectionPullRequests);  // show again

        // Same instance AND no extra load: the kept-alive screen is reused, not rebuilt+reloaded, so
        // the rows are not refetched. (LoadCount is the sole refetch trigger.)
        Assert.Same(first, shell.PrListScreen);
        Assert.Equal(loadsAfterFirstShow, shell.PrListScreen!.LoadCount);
    }

    [Fact]
    public void Scope_Change_Reloads_The_Hidden_Pr_Screen()
    {
        var vm = Vm(PrScope.Org);
        using var shell = new CobaltShell(App, vm, pullRequests: Adapter(new CountingHandler()));

        vm.HandleCommand(AppCommand.SectionPullRequests);
        var pr = shell.PrListScreen!;
        var loadsBefore = pr.LoadCount;
        vm.HandleCommand(AppCommand.SectionWorkItems); // PR list now hidden but kept alive

        // A :scope flip changes the query for every tab, so even the hidden PR screen must reload
        // (ReloadFromTop) — otherwise switching back would show stale org-scope rows.
        vm.HandlePaletteInput("scope project");

        Assert.True(pr.LoadCount > loadsBefore, $"expected a reload of the hidden screen, {loadsBefore} → {pr.LoadCount}");
    }

    [Fact]
    public void Tab_In_Pr_Section_Without_A_Built_List_Toggles_The_Section()
    {
        var vm = Vm();
        using var shell = new CobaltShell(App, vm); // no adapters → placeholder in both sections
        vm.HandleCommand(AppCommand.SectionPullRequests);
        Assert.Equal(AppSection.PullRequests, vm.ActiveSection);
        Assert.Null(shell.PrListScreen); // unauthenticated: no PR list built
        shell.SetFocus();

        shell.NewKeyDownEvent(new Terminal.Gui.Input.Key(Terminal.Gui.Drivers.KeyCode.Tab));

        // With no PR list to cycle sub-tabs, Tab must fall through to a top-level section toggle,
        // not be swallowed by the PR-tab gate.
        Assert.Equal(AppSection.WorkItems, vm.ActiveSection);
    }

    // ---- M5 workspace Tab (ADR 0024): CyclePane consumed when the preview shows, today's
    // ---- semantics otherwise ----

    [Fact]
    public void Tab_In_Pr_Section_With_A_Built_List_Still_Cycles_Sub_Tabs()
    {
        // Regression pin, green before and after M5: with the preview hidden the workspace
        // declines Tab (CyclePane → false) and the shell falls back to the PR sub-tab cycle.
        var vm = Vm();
        using var shell = new CobaltShell(App, vm, pullRequests: Adapter(new CountingHandler()));
        vm.HandleCommand(AppCommand.SectionPullRequests);
        shell.SetFocus();
        Assert.Equal(PrListFilter.Team, shell.PrListVm!.ActiveTab);

        shell.NewKeyDownEvent(new Terminal.Gui.Input.Key(Terminal.Gui.Drivers.KeyCode.Tab));

        Assert.Equal(PrListFilter.Mine, shell.PrListVm!.ActiveTab); // Team → Mine
        Assert.Equal(AppSection.PullRequests, vm.ActiveSection);    // no section toggle
    }

    [Fact]
    public void Tab_With_A_Visible_Preview_Is_Consumed_As_Pane_Focus()
    {
        // The M5 flip: once the preview is visible (#48 will drive this from the layout),
        // Tab toggles workspace pane focus instead of cycling PR sub-tabs or sections.
        var vm = Vm();
        using var shell = new CobaltShell(App, vm, pullRequests: Adapter(new CountingHandler()));
        vm.HandleCommand(AppCommand.SectionPullRequests);
        shell.SetFocus();
        shell.Workspace.SetPreviewVisible(true);

        shell.NewKeyDownEvent(new Terminal.Gui.Input.Key(Terminal.Gui.Drivers.KeyCode.Tab));

        Assert.Equal(WorkspacePane.Preview, shell.Workspace.FocusedPane);
        Assert.Equal(PrListFilter.Team, shell.PrListVm!.ActiveTab);  // sub-tabs untouched
        Assert.Equal(AppSection.PullRequests, vm.ActiveSection);     // no section toggle
    }

    // ---- INPUT-1: targeted redraw on vim movement (both-driver UAT still required) ----

    [Fact]
    public void Vim_Move_Dirties_The_Moved_List_And_Uses_A_Non_Forced_Redraw()
    {
        var vm = Vm();
        using var shell = new CobaltShell(App, vm, pullRequests: Adapter(new CountingHandler()));
        vm.HandleCommand(AppCommand.SectionPullRequests);
        shell.Layout(new System.Drawing.Size(80, 24));
        shell.SetFocus();

        // Clear the moved list's draw flag so the post-move assertion is not vacuous — a freshly
        // built view is already NeedsDraw, and headless never draws to clear it. Deleting the
        // SetNeedsDraw in the shell must then fail this test.
        var list = shell.PrListScreen!;
        ClearNeedsDraw(list);
        Assert.False(list.NeedsDraw); // baseline: clean

        bool? forced = null;
        shell.MovementRedrawOverride = force => forced = force; // observe the redraw kind, don't paint

        shell.NewKeyDownEvent(new Terminal.Gui.Input.Key('j'));

        // The move flips the moved list to needing redraw, and the repaint is non-forced
        // (LayoutAndDraw(false)) instead of forcing a full-app repaint on every keystroke.
        Assert.True(list.NeedsDraw);
        Assert.Equal(false, forced);
    }
}
