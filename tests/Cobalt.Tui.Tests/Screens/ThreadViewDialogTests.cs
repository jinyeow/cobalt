using System.Drawing;
using Cobalt.Core.Models;
using Cobalt.Tui.Editor;
using Cobalt.Tui.Screens;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// View-level, headless: builds the interactive thread overlay and drives keys through the
/// real Terminal.Gui routing. Guards the ThreadView verbs — c reply, x resolve, u reactivate,
/// q close — via the injectable action seams (the real paths need the editor / app.Invoke).
/// </summary>
public class ThreadViewDialogTests
{
    private static readonly IApplication App = Application.Create();

    private sealed class FakeLauncher : IEditorLauncher
    {
        public Task<int> LaunchAsync(string path, CancellationToken ct) => Task.FromResult(0);
    }

    private static EditorService NoopEditor() => new(new FakeLauncher());

    private sealed class FakeDiffSource : IPrDiffSource
    {
        public Task<PrIteration?> GetLatestIterationAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult<PrIteration?>(new(1, "src", "tgt", "base"));
        public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repo, int prId, int iterationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FileChange>>([]);
        public Task<string> GetFileContentAsync(string project, string repo, string path, string commit, CancellationToken ct) =>
            Task.FromResult("");
        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PrThread>>([]);
        public Task AddLineCommentAsync(string project, string repo, int prId, string path, int line, bool right, string text, CancellationToken ct) =>
            Task.CompletedTask;
        public Task ReplyToThreadAsync(string project, string repo, int prId, int threadId, string text, CancellationToken ct) =>
            Task.CompletedTask;
        public Task SetThreadStatusAsync(string project, string repo, int prId, int threadId, PrThreadStatus status, CancellationToken ct) =>
            Task.CompletedTask;
        public Task VoteAsync(string project, string repo, int prId, PrVote vote, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private static PullRequest Pr() =>
        new(10, "t", null, "active", false, "f", "main", "succeeded", "Jin", "repo-1", "web", [], [], "src", "Contoso.Web");

    private static PrDiffViewModel Vm() => new(new FakeDiffSource(), Pr());

    private static PrThread Thread(int id, PrThreadStatus status = PrThreadStatus.Active) =>
        new(id, status, [new PrComment(1, "Sam", "looks good to me", false)], "/a.cs", RightLine: 2, LeftLine: null);

    private static (ThreadViewDialog View, Dialog Dialog) Built(params PrThread[] threads)
    {
        var view = new ThreadViewDialog(App, Vm(), NoopEditor(), _ => { }, threads);
        var dialog = view.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();
        return (view, dialog);
    }

    [Fact]
    public void Body_Shows_The_Comment_Text()
    {
        var (view, _) = Built(Thread(7));

        Assert.Contains("looks good to me", view.Body.Text);
        Assert.Contains("#7", view.Body.Text);
    }

    [Fact]
    public void C_Invokes_Reply()
    {
        var (view, dialog) = Built(Thread(7));
        var replied = false;
        view.ReplyAction = () => replied = true;

        dialog.NewKeyDownEvent(new Key('c'));

        Assert.True(replied);
    }

    [Fact]
    public void X_Invokes_Resolve()
    {
        var (view, dialog) = Built(Thread(7));
        var resolved = false;
        view.ResolveAction = () => resolved = true;

        dialog.NewKeyDownEvent(new Key('x'));

        Assert.True(resolved);
    }

    [Fact]
    public void U_Invokes_Reactivate()
    {
        var (view, dialog) = Built(Thread(7, PrThreadStatus.Fixed));
        var reactivated = false;
        view.ReactivateAction = () => reactivated = true;

        dialog.NewKeyDownEvent(new Key('u'));

        Assert.True(reactivated);
    }

    [Fact]
    public void Q_Closes_The_Overlay()
    {
        var (view, dialog) = Built(Thread(7));
        var closed = false;
        view.CloseAction = () => closed = true;

        dialog.NewKeyDownEvent(new Key('q'));

        Assert.True(closed);
    }

    [Fact]
    public void Multiple_Threads_Act_On_The_First()
    {
        var (view, _) = Built(Thread(7), Thread(9));

        Assert.Equal(7, view.TargetThreadId); // actions target the first anchored thread
        Assert.Contains("#9", view.Body.Text); // but every thread is shown
    }
}
