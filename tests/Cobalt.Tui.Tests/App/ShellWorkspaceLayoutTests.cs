using System.Drawing;
using System.Net;
using System.Text;
using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Tui.App;
using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// The shell's list+preview split (#48, ADR 0024): the geometry comes only from
/// <see cref="WorkspaceLayout.Compute"/>, the <c>preview = off</c> override is applied here in
/// the shell, and below the collapse threshold the content area is exactly today's full-width
/// list. Headless: the shell is laid out directly, no driver.
/// </summary>
public class ShellWorkspaceLayoutTests
{
    private static readonly IApplication App = Application.Create();

    private static readonly AdoContext Context = new()
    {
        Name = "work",
        OrganizationUrl = new Uri("https://dev.azure.com/contoso"),
        Project = "Proj",
    };

    /// <summary>Always answers an empty PR list, so the shell builds a real list screen.</summary>
    private sealed class EmptyListHandler : HttpMessageHandler
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

    private static PullRequestStoreAdapter Adapter()
    {
        var httpClient = new HttpClient(new EmptyListHandler()) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        return new PullRequestStoreAdapter(new GitApi(new AdoHttp(httpClient), Context), _ => Task.FromResult(Guid.Empty), project: Context.Project);
    }

    /// <summary>A shell showing the PR list (a real list screen), laid out at <paramref name="width"/>.</summary>
    private static CobaltShell LaidOutShell(int width, PreviewMode preview = PreviewMode.Auto, int height = 24)
    {
        var vm = new ShellViewModel(["work"], "work", PrScope.Org, ThemeChoice.Dark, preview);
        var shell = new CobaltShell(App, vm, pullRequests: Adapter());
        vm.HandleCommand(AppCommand.SectionPullRequests);
        shell.Layout(new Size(width, height));
        return shell;
    }

    [Fact]
    public void A_Wide_Terminal_Splits_The_Content_Into_List_And_Preview()
    {
        // 120 cols: the wide band gives the list clamp(120*0.45, 40, 70) = 54 columns, the
        // preview the remaining 66 — the numbers are WorkspaceLayout's, not the shell's.
        using var shell = LaidOutShell(120);

        Assert.True(shell.Workspace.PreviewVisible);
        Assert.True(shell.PreviewScreen.Visible);
        Assert.Equal(54, shell.PrListScreen!.Frame.Width);
        Assert.Equal(54, shell.PreviewScreen.Frame.X);
        Assert.Equal(66, shell.PreviewScreen.Frame.Width);
    }

    [Fact]
    public void A_Narrow_Terminal_Collapses_To_Todays_Full_Width_List()
    {
        // Below the 100-col threshold the preview is gone and the list owns the whole row —
        // exactly the pre-workspace UX on an 80x24 terminal.
        using var shell = LaidOutShell(80);

        Assert.False(shell.Workspace.PreviewVisible);
        Assert.False(shell.PreviewScreen.Visible);
        Assert.Equal(80, shell.PrListScreen!.Frame.Width);
    }

    [Fact]
    public void Preview_Off_Collapses_Even_On_A_Wide_Terminal()
    {
        // The `off` override is applied in the shell; WorkspaceLayout stays width-pure.
        using var shell = LaidOutShell(120, PreviewMode.Off);

        Assert.False(shell.Workspace.PreviewVisible);
        Assert.False(shell.PreviewScreen.Visible);
        Assert.Equal(120, shell.PrListScreen!.Frame.Width);
    }

    [Fact]
    public void Resizing_Below_The_Threshold_Collapses_The_Preview()
    {
        using var shell = LaidOutShell(120);
        Assert.True(shell.Workspace.PreviewVisible);

        shell.Layout(new Size(80, 24));

        Assert.False(shell.Workspace.PreviewVisible);
        Assert.Equal(80, shell.PrListScreen!.Frame.Width);
    }

    /// <summary>Fills the preview with more lines than it can show, budget lifted, so a scroll
    /// has somewhere to go (#48 itself never sets content — #49 wires the source).</summary>
    private static void FillPreview(CobaltShell shell)
    {
        shell.PreviewScreen.LineBudgetOverride = 500;
        shell.PreviewScreen.SetContent(string.Join("\n", Enumerable.Range(0, 100).Select(i => $"line {i}")));
    }

    [Fact]
    public void With_The_Preview_Focused_Movement_Scrolls_The_Preview()
    {
        // Once Tab can land focus on the preview, movement must follow it there — otherwise
        // the focused pane is a trap that j/k cannot move.
        using var shell = LaidOutShell(120);
        FillPreview(shell);
        shell.SetFocus();
        shell.NewKeyDownEvent(new Key(KeyCode.Tab));
        Assert.Equal(WorkspacePane.Preview, shell.Workspace.FocusedPane);

        shell.NewKeyDownEvent(new Key('j'));
        Assert.Equal(1, shell.PreviewScreen.Body.CurrentRow);

        shell.NewKeyDownEvent(new Key('G'));
        Assert.True(shell.PreviewScreen.Body.CurrentRow > 1, "G should scroll the preview to the bottom");
    }

    [Fact]
    public void With_The_List_Focused_Movement_Leaves_The_Preview_Alone()
    {
        using var shell = LaidOutShell(120);
        FillPreview(shell);
        shell.SetFocus();
        Assert.Equal(WorkspacePane.List, shell.Workspace.FocusedPane);

        shell.NewKeyDownEvent(new Key('j'));

        Assert.Equal(0, shell.PreviewScreen.Body.CurrentRow);
    }

    [Fact]
    public void Focus_Returns_To_The_List_When_The_Preview_Collapses()
    {
        using var shell = LaidOutShell(120);
        shell.SetFocus();
        shell.NewKeyDownEvent(new Key(KeyCode.Tab));
        Assert.Equal(WorkspacePane.Preview, shell.Workspace.FocusedPane);

        shell.Layout(new Size(80, 24)); // collapse below the threshold

        Assert.Equal(WorkspacePane.List, shell.Workspace.FocusedPane);
        Assert.True(shell.PrListScreen!.HasFocus);
    }

    [Fact]
    public void Palette_Preview_Off_Collapses_The_Split_Live()
    {
        var vm = new ShellViewModel(["work"], "work", PrScope.Org, ThemeChoice.Dark, PreviewMode.Auto);
        using var shell = new CobaltShell(App, vm, pullRequests: Adapter());
        vm.HandleCommand(AppCommand.SectionPullRequests);
        shell.Layout(new Size(120, 24));
        Assert.True(shell.Workspace.PreviewVisible);

        vm.HandlePaletteInput("preview off");

        Assert.False(shell.Workspace.PreviewVisible);
        Assert.False(shell.PreviewScreen.Visible);
        Assert.Equal(120, shell.PrListScreen!.Frame.Width);

        vm.HandlePaletteInput("preview auto"); // and back

        Assert.True(shell.Workspace.PreviewVisible);
        Assert.Equal(54, shell.PrListScreen!.Frame.Width);
    }

    [Fact]
    public void The_Keybar_Advertises_Tab_Only_While_The_Preview_Shows()
    {
        // Width 400: the bar is width-fitted and CyclePane sits late in bind order, so a
        // realistic width truncates the entry before it can be asserted on.
        using var shell = LaidOutShell(400);
        Assert.Contains("Tab:switch list / preview", shell.KeybarText);

        shell.Layout(new Size(80, 24));

        Assert.DoesNotContain("switch list / preview", shell.KeybarText);
    }
}
