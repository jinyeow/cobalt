using Cobalt.Tui.Editor;

namespace Cobalt.Tui.Tests.Editor;

public class ProcessEditorLauncherTests
{
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
