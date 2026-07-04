using Cobalt.Core.Cli;

namespace Cobalt.Core.Tests.Cli;

public class CliArgsTests
{
    [Fact]
    public void NoArgs_LaunchesTui()
    {
        var result = CliArgs.Parse([]);

        Assert.Equal(CliCommand.Tui, result.Command);
        Assert.Null(result.Context);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void VersionFlag_SelectsVersionCommand(string flag)
    {
        var result = CliArgs.Parse([flag]);

        Assert.Equal(CliCommand.Version, result.Command);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpFlag_SelectsHelpCommand(string flag)
    {
        var result = CliArgs.Parse([flag]);

        Assert.Equal(CliCommand.Help, result.Command);
    }

    [Fact]
    public void ContextOption_SetsContextAndLaunchesTui()
    {
        var result = CliArgs.Parse(["--context", "work"]);

        Assert.Equal(CliCommand.Tui, result.Command);
        Assert.Equal("work", result.Context);
    }

    [Fact]
    public void ContextOption_WithoutValue_IsAnError()
    {
        var result = CliArgs.Parse(["--context"]);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void AuthLogin_IsRecognized()
    {
        var result = CliArgs.Parse(["auth", "login"]);

        Assert.Equal(CliCommand.AuthLogin, result.Command);
    }

    [Fact]
    public void AuthStatus_IsRecognized()
    {
        var result = CliArgs.Parse(["auth", "status"]);

        Assert.Equal(CliCommand.AuthStatus, result.Command);
    }

    [Fact]
    public void AuthStatus_WithContext_CombinesBoth()
    {
        var result = CliArgs.Parse(["--context", "oss", "auth", "status"]);

        Assert.Equal(CliCommand.AuthStatus, result.Command);
        Assert.Equal("oss", result.Context);
    }

    [Fact]
    public void Auth_WithoutSubcommand_IsAnError()
    {
        var result = CliArgs.Parse(["auth"]);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void UnknownArgument_IsAnError()
    {
        var result = CliArgs.Parse(["frobnicate"]);

        Assert.NotNull(result.Error);
        Assert.Contains("frobnicate", result.Error);
    }
}
