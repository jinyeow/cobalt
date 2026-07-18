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
            finally
            {
                log.Add("exit");
            }
        }
    }

    private static Func<string, string?> Env(string editor) =>
        name => name == "EDITOR" ? editor : null;

    /// <summary>
    /// Some tests use a POSIX command (<c>sh</c>, <c>true</c>) as a portable stand-in editor.
    /// Those exist on Linux/macOS and on CI's Windows runners (Git Bash), but not on a bare
    /// Windows box — so the test skips there rather than fails. cobalt itself never invokes a
    /// shell at runtime: it launches the user's configured <c>$EDITOR</c>, nothing more.
    /// </summary>
    private static bool CommandOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(dir => !string.IsNullOrEmpty(dir))
            .Any(dir => File.Exists(Path.Combine(dir, name)) || File.Exists(Path.Combine(dir, name + ".exe")));

    [Fact]
    public async Task Process_Runs_Inside_Suspender_Body()
    {
        Assert.SkipUnless(CommandOnPath("sh"), "requires 'sh' on PATH");
        var log = new List<string>();
        var launcher = new ProcessEditorLauncher(Env("sh -c 'exit 3'"), new RecordingSuspender(log));
        var path = Path.Join(Path.GetTempPath(), $"cobalt-test-{Guid.NewGuid():N}.txt");

        var exit = await launcher.LaunchAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(3, exit);
        Assert.Equal(["enter", "exit"], log);
    }

    [Fact]
    public async Task Editor_Writes_File_Through_Suspender()
    {
        Assert.SkipUnless(CommandOnPath("sh"), "requires 'sh' on PATH");
        var log = new List<string>();
        var launcher = new ProcessEditorLauncher(Env("sh -c 'echo edited > \"$0\"'"), new RecordingSuspender(log));
        var path = Path.Join(Path.GetTempPath(), $"cobalt-test-{Guid.NewGuid():N}.txt");
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
        var path = Path.Join(Path.GetTempPath(), $"cobalt-test-{Guid.NewGuid():N}.txt");

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
        var path = Path.Join(Path.GetTempPath(), $"cobalt-test-{Guid.NewGuid():N}.txt");

        await Assert.ThrowsAsync<EditorLaunchException>(
            () => launcher.LaunchAsync(path, TestContext.Current.CancellationToken));

        Assert.Equal("resume", log[^1]);
    }

    [Fact]
    public async Task Default_Suspender_Is_Inline()
    {
        Assert.SkipUnless(CommandOnPath("true"), "requires 'true' on PATH");
        var launcher = new ProcessEditorLauncher(Env("true"));
        var path = Path.Join(Path.GetTempPath(), $"cobalt-test-{Guid.NewGuid():N}.txt");
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
