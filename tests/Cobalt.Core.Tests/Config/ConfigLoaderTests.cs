using Cobalt.Core.Config;

namespace Cobalt.Core.Tests.Config;

public class ConfigLoaderTests
{
    private const string ValidToml =
        """
        default_context = "work"

        [contexts.work]
        organization = "https://dev.azure.com/contoso"
        project = "Platform"

        [contexts.oss]
        organization = "fabrikam"
        project = "Tools"
        """;

    [Fact]
    public void Parses_Contexts_And_Default()
    {
        var config = ConfigLoader.Parse(ValidToml);

        Assert.Equal("work", config.DefaultContext);
        Assert.Equal(2, config.Contexts.Count);
        Assert.Equal("Platform", config.Contexts["work"].Project);
    }

    [Fact]
    public void Normalizes_Bare_Org_Name_To_DevAzureCom_Url()
    {
        var config = ConfigLoader.Parse(ValidToml);

        Assert.Equal(new Uri("https://dev.azure.com/fabrikam"), config.Contexts["oss"].OrganizationUrl);
    }

    [Fact]
    public void Preserves_Full_Org_Url()
    {
        var config = ConfigLoader.Parse(ValidToml);

        Assert.Equal(new Uri("https://dev.azure.com/contoso"), config.Contexts["work"].OrganizationUrl);
    }

    [Fact]
    public void Missing_DefaultContext_Falls_Back_To_Single_Context()
    {
        var config = ConfigLoader.Parse(
            """
            [contexts.only]
            organization = "contoso"
            project = "P"
            """);

        var ctx = config.Resolve(null);

        Assert.Equal("only", ctx.Name);
    }

    [Fact]
    public void Resolve_Uses_Cli_Override_Over_Default()
    {
        var config = ConfigLoader.Parse(ValidToml);

        Assert.Equal("oss", config.Resolve("oss").Name);
        Assert.Equal("work", config.Resolve(null).Name);
    }

    [Fact]
    public void Resolve_Unknown_Context_Throws_With_Available_Names()
    {
        var config = ConfigLoader.Parse(ValidToml);

        var ex = Assert.Throws<ConfigException>(() => config.Resolve("nope"));
        Assert.Contains("work", ex.Message);
        Assert.Contains("oss", ex.Message);
    }

    [Fact]
    public void DefaultContext_Pointing_Nowhere_Is_An_Error()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(
            """
            default_context = "ghost"

            [contexts.work]
            organization = "contoso"
            project = "P"
            """));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void Context_Missing_Project_Is_An_Error()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(
            """
            [contexts.work]
            organization = "contoso"
            """));
        Assert.Contains("project", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_Contexts_Is_An_Error()
    {
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse("default_context = \"x\""));
    }

    [Fact]
    public void Non_Https_Org_Url_Is_An_Error()
    {
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse(
            """
            [contexts.work]
            organization = "http://dev.azure.com/contoso"
            project = "P"
            """));
    }

    [Fact]
    public void Invalid_Toml_Reports_ConfigException()
    {
        Assert.Throws<ConfigException>(() => ConfigLoader.Parse("this is [not toml"));
    }
}
