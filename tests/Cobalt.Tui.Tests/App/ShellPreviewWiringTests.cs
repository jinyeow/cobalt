using System.Drawing;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Tui.App;
using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;
using Microsoft.Extensions.Time.Testing;
using Terminal.Gui.App;
using Terminal.Gui.Input;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// The shell end of the preview pipeline (#49, ADR 0024): the highlighted row paints into the
/// pane immediately from the data the list already holds, and only a settled cursor spends a
/// round-trip. Headless: a fake clock drives the debounce, a fake ADO endpoint counts the
/// detail fetches, and <see cref="RecordingUiPost"/> drains the UI-thread hops.
/// </summary>
public class ShellPreviewWiringTests
{
    private static readonly IApplication App = Application.Create();

    private static readonly AdoContext Context = new()
    {
        Name = "work",
        OrganizationUrl = new Uri("https://dev.azure.com/contoso"),
        Project = "Proj",
    };

    private const string ListJson = """
        {"value":[
          {"pullRequestId":1,"title":"first","status":"active","sourceRefName":"refs/heads/a","targetRefName":"refs/heads/main",
           "repository":{"id":"r","name":"web","project":{"id":"p","name":"Proj"}},"reviewers":[]},
          {"pullRequestId":2,"title":"second","status":"active","sourceRefName":"refs/heads/b","targetRefName":"refs/heads/main",
           "repository":{"id":"r","name":"web","project":{"id":"p","name":"Proj"}},"reviewers":[]}
        ]}
        """;

    /// <summary>PR 1's detail carries a description the list row does not — the tell that tier 2 landed.</summary>
    private const string DetailBody = "only the detail knows this";

    private const string DetailJson = $$$"""
        {"pullRequestId":1,"title":"first","status":"active","sourceRefName":"refs/heads/a","targetRefName":"refs/heads/main",
         "description":"{{{DetailBody}}}",
         "repository":{"id":"r","name":"web","project":{"id":"p","name":"Proj"}},"reviewers":[]}
        """;

    /// <summary>Answers the PR list, PR detail, threads and policy routes, counting detail fetches.</summary>
    private sealed class PrHandler : HttpMessageHandler
    {
        private int _detailCalls;

        /// <summary>How many times the single-PR detail route was hit — a tier-2 fetch, never tier 1.</summary>
        public int DetailCalls => Volatile.Read(ref _detailCalls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            var path = request.RequestUri!.AbsolutePath.ToLowerInvariant();
            string json;
            if (path.Contains("/threads") || path.Contains("/policy/evaluations"))
            {
                json = """{"value":[]}""";
            }
            else if (Regex.IsMatch(path, @"/pullrequests/\d+$"))
            {
                Interlocked.Increment(ref _detailCalls);
                json = DetailJson;
            }
            else
            {
                json = ListJson;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static PullRequestStoreAdapter Adapter(PrHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        var http = new AdoHttp(httpClient);
        // A team resolver is required for the default Team tab, and the policy API for the detail
        // load the preview's tier 2 runs — both are wired exactly as the composition root does.
        return new PullRequestStoreAdapter(
            new GitApi(http, Context),
            _ => Task.FromResult(Guid.Empty),
            _ => Task.FromResult(new TeamDirectory([])),
            project: Context.Project,
            policy: new PolicyApi(http));
    }

    private sealed record Harness(CobaltShell Shell, PrHandler Handler, RecordingUiPost Post, FakeTimeProvider Time)
        : IDisposable
    {
        public string PaneText => Shell.PreviewScreen.Body.Text.ReplaceLineEndings("\n");

        /// <summary>Drains queued UI hops until the pane says <paramref name="text"/>. A tier-2
        /// publish lands on a background continuation and only then posts its repaint, so a single
        /// drain can run before the repaint was even queued.</summary>
        public async Task DrainUntilPaneSaysAsync(string text)
        {
            for (var i = 0; i < 300 && !PaneText.Contains(text); i++)
            {
                Post.RunAll();
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            Post.RunAll();
            Assert.Contains(text, PaneText);
        }

        public void Dispose() => Shell.Dispose();
    }

    /// <summary>A shell on the PR section with two loaded rows, laid out at <paramref name="width"/>
    /// and with every queued UI hop drained.</summary>
    private static async Task<Harness> LoadedShellAsync(int width = 120)
    {
        var handler = new PrHandler();
        var post = new RecordingUiPost();
        var time = new FakeTimeProvider();
        var vm = new ShellViewModel(["work"], "work", PrScope.Org, ThemeChoice.Dark, PreviewMode.Auto);
        var shell = new CobaltShell(App, vm, pullRequests: Adapter(handler), post: post, time: time);
        vm.HandleCommand(AppCommand.SectionPullRequests);
        shell.Layout(new Size(width, 24));
        // The default Team tab is a union filtered by team membership (empty here); the Mine tab is
        // a plain server-side query, which is what this fake endpoint answers.
        shell.PrListScreen!.NextTab();
        await WaitForAsync(
            () => shell.PrListVm?.Rows.Count == 2,
            () => $"rows never loaded (count={shell.PrListVm?.Rows.Count}, error={shell.PrListVm?.Error}, loading={shell.PrListVm?.IsLoading}, tab={shell.PrListVm?.ActiveTab})");
        post.RunAll();
        return new Harness(shell, handler, post, time);
    }

    /// <summary>Waits for a background load/continuation to land (real time — the fake clock only
    /// drives the debounce).</summary>
    private static async Task WaitForAsync(Func<bool> condition, Func<string>? what = null)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), what?.Invoke() ?? "the expected state never arrived");
    }

    [Fact]
    public async Task The_Highlighted_Row_Paints_Instantly_And_Costs_No_Fetch()
    {
        // Tier 1 (ADR 0024 fork B): the pane fills from data the list row already holds.
        using var harness = await LoadedShellAsync();

        await harness.DrainUntilPaneSaysAsync("!1  first");
        Assert.Equal(0, harness.Handler.DetailCalls);
    }

    [Fact]
    public async Task Only_A_Settled_Cursor_Spends_A_Round_Trip()
    {
        using var harness = await LoadedShellAsync();
        await harness.DrainUntilPaneSaysAsync("!1  first");
        Assert.Equal(0, harness.Handler.DetailCalls);

        harness.Time.Advance(PreviewViewModel.DefaultDebounce);
        await harness.DrainUntilPaneSaysAsync(DetailBody);

        Assert.Equal(1, harness.Handler.DetailCalls); // one settle, one round-trip
    }

    [Fact]
    public async Task Moving_The_Cursor_Repaints_The_Pane_For_The_New_Row()
    {
        using var harness = await LoadedShellAsync();
        await harness.DrainUntilPaneSaysAsync("!1  first");
        harness.Shell.SetFocus();

        harness.Shell.NewKeyDownEvent(new Key('j'));

        await harness.DrainUntilPaneSaysAsync("!2  second");
        Assert.Equal(0, harness.Handler.DetailCalls); // still moving: nothing enqueued
    }

    [Fact]
    public async Task Collapsing_The_Preview_Cancels_A_Debounce_Already_Armed()
    {
        // A visible preview arms a tier-2 debounce; collapsing below the threshold must cancel it,
        // not let it fire on the org's behalf while the pane is hidden (ADR 0024).
        using var harness = await LoadedShellAsync(width: 120);
        await harness.DrainUntilPaneSaysAsync("!1  first"); // tier 1 up, debounce armed, not yet settled
        Assert.Equal(0, harness.Handler.DetailCalls);

        harness.Shell.Layout(new Size(80, 24)); // collapse below the threshold
        harness.Time.Advance(PreviewViewModel.DefaultDebounce);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        harness.Post.RunAll();

        Assert.Equal(0, harness.Handler.DetailCalls);
    }

    [Fact]
    public async Task A_Collapsed_Preview_Neither_Paints_Nor_Fetches()
    {
        // Below the collapse threshold the workspace is exactly today's full-width list, so the
        // preview must not spend a round-trip on the org's behalf either.
        using var harness = await LoadedShellAsync(width: 80);

        harness.Time.Advance(PreviewViewModel.DefaultDebounce);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        harness.Post.RunAll();

        Assert.Equal("(no preview)", harness.PaneText);
        Assert.Equal(0, harness.Handler.DetailCalls);
    }
}
