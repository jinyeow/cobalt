using Cobalt.Tui.Editor;

namespace Cobalt.Tui.Tests.Editor;

public class EditorServiceTests
{
    private sealed class FakeLauncher : IEditorLauncher
    {
        public Func<string, int> OnLaunch { get; init; } = _ => 0;
        public string? LaunchedPath { get; private set; }

        public Task<int> LaunchAsync(string filePath, CancellationToken cancellationToken = default)
        {
            LaunchedPath = filePath;
            return Task.FromResult(OnLaunch(filePath));
        }
    }

    [Fact]
    public async Task Returns_Edited_Text_When_File_Changed()
    {
        var launcher = new FakeLauncher { OnLaunch = path => { File.WriteAllText(path, "edited\n"); return 0; } };
        var service = new EditorService(launcher);

        var result = await service.EditAsync("original\n", ".md", TestContext.Current.CancellationToken);

        Assert.Equal("edited\n", result);
    }

    [Fact]
    public async Task Returns_Null_When_Unchanged()
    {
        var launcher = new FakeLauncher();
        var service = new EditorService(launcher);

        var result = await service.EditAsync("original\n", ".md", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task Returns_Null_When_Editor_Exits_NonZero()
    {
        var launcher = new FakeLauncher { OnLaunch = path => { File.WriteAllText(path, "edited\n"); return 1; } };
        var service = new EditorService(launcher);

        var result = await service.EditAsync("original\n", ".md", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task Temp_File_Uses_Requested_Extension_And_Is_Cleaned_Up()
    {
        var launcher = new FakeLauncher();
        var service = new EditorService(launcher);

        await service.EditAsync("x", ".md", TestContext.Current.CancellationToken);

        Assert.EndsWith(".md", launcher.LaunchedPath!);
        Assert.False(File.Exists(launcher.LaunchedPath));
    }

    [Fact]
    public void Editor_Command_Prefers_VISUAL_Then_EDITOR_Then_Vi()
    {
        Assert.Equal("nvim", EditorService.ResolveEditorCommand(n =>
            n switch { "VISUAL" => "nvim", "EDITOR" => "nano", _ => null }));
        Assert.Equal("nano", EditorService.ResolveEditorCommand(n =>
            n == "EDITOR" ? "nano" : null));
        Assert.Equal("vi", EditorService.ResolveEditorCommand(_ => null));
    }
}
