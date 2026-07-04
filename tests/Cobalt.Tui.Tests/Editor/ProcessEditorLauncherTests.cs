using Cobalt.Tui.Editor;

namespace Cobalt.Tui.Tests.Editor;

public class ProcessEditorLauncherTests
{
    private sealed class RecordingSuspender(List<string> log) : ITerminalSuspender
    {
        public Task<int> RunSuspendedAsync(Func<int> body, CancellationToken cancellationToken = default)
        {
            log.Add("enter");
            try
            {
                return Task.FromResult(body());
            }
            catch (Exception ex)
            {
                return Task.FromException<int>(ex);
            }
            finally
            {
                log.Add("exit");
            }
        }
    }

    private static Func<string, string?> Env(string editor) =>
        name => name == "EDITOR" ? editor : null;

    [Fact]
    public async Task Process_Runs_Inside_Suspender_Body()
    {
        var log = new List<string>();
        var launcher = new ProcessEditorLauncher(Env("sh -c 'exit 3'"), new RecordingSuspender(log));
        var path = Path.Combine(Path.GetTempPath(), $"cobalt-test-{Guid.NewGuid():N}.txt");

        var exit = await launcher.LaunchAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(3, exit);
        Assert.Equal(["enter", "exit"], log);
    }

    [Fact]
    public async Task Editor_Writes_File_Through_Suspender()
    {
        var log = new List<string>();
        var launcher = new ProcessEditorLauncher(Env("sh -c 'echo edited > \"$0\"'"), new RecordingSuspender(log));
        var path = Path.Combine(Path.GetTempPath(), $"cobalt-test-{Guid.NewGuid():N}.txt");
        try
        {
            var exit = await launcher.LaunchAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(0, exit);
            Assert.Equal("edited", (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Missing_Editor_Throws_EditorLaunchException()
    {
        var launcher = new ProcessEditorLauncher(
            Env("cobalt-no-such-editor-xyz"), new RecordingSuspender([]));
        var path = Path.Combine(Path.GetTempPath(), $"cobalt-test-{Guid.NewGuid():N}.txt");

        await Assert.ThrowsAsync<EditorLaunchException>(
            () => launcher.LaunchAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resume_Still_Fires_When_Editor_Is_Missing()
    {
        var log = new List<string>();
        var suspender = new UiThreadSuspender(
            a => a(),
            () => log.Add("suspend"),
            () => log.Add("resume"));
        var launcher = new ProcessEditorLauncher(Env("cobalt-no-such-editor-xyz"), suspender);
        var path = Path.Combine(Path.GetTempPath(), $"cobalt-test-{Guid.NewGuid():N}.txt");

        await Assert.ThrowsAsync<EditorLaunchException>(
            () => launcher.LaunchAsync(path, TestContext.Current.CancellationToken));

        Assert.Equal("resume", log[^1]);
    }

    [Fact]
    public async Task Default_Suspender_Is_Inline()
    {
        var launcher = new ProcessEditorLauncher(Env("true"));
        var path = Path.Combine(Path.GetTempPath(), $"cobalt-test-{Guid.NewGuid():N}.txt");
        try
        {
            var exit = await launcher.LaunchAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(0, exit);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Plain_Command()
    {
        Assert.Equal(["nvim"], ProcessEditorLauncher.TokenizeCommand("nvim"));
    }

    [Fact]
    public void Command_With_Args()
    {
        Assert.Equal(["code", "--wait"], ProcessEditorLauncher.TokenizeCommand("code --wait"));
    }

    [Fact]
    public void Quoted_Path_With_Spaces_Stays_One_Token()
    {
        Assert.Equal(["/opt/My Editor/nvim", "--wait"],
            ProcessEditorLauncher.TokenizeCommand("\"/opt/My Editor/nvim\" --wait"));
    }

    [Fact]
    public void Single_Quotes_Also_Honored()
    {
        Assert.Equal(["my editor"], ProcessEditorLauncher.TokenizeCommand("'my editor'"));
    }

    [Fact]
    public void Empty_Falls_Back_To_Vi()
    {
        Assert.Equal(["vi"], ProcessEditorLauncher.TokenizeCommand("   "));
    }
}
