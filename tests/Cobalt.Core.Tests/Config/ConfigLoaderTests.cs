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

    [Fact]
    public void PrScope_Defaults_To_Org_When_Unset()
    {
        var config = ConfigLoader.Parse(ValidToml);

        Assert.Equal(PrScope.Org, config.Contexts["work"].PrScope);
    }

    [Fact]
    public void PrScope_Project_Is_Parsed()
    {
        var config = ConfigLoader.Parse(
            """
            [contexts.work]
            organization = "contoso"
            project = "P"
            pr_scope = "project"
            """);

        Assert.Equal(PrScope.Project, config.Contexts["work"].PrScope);
    }

    [Fact]
    public void PrScope_Org_Is_Parsed()
    {
        var config = ConfigLoader.Parse(
            """
            [contexts.work]
            organization = "contoso"
            project = "P"
            pr_scope = "org"
            """);

        Assert.Equal(PrScope.Org, config.Contexts["work"].PrScope);
    }

    [Fact]
    public void PrScope_Invalid_Value_Is_An_Error()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(
            """
            [contexts.work]
            organization = "contoso"
            project = "P"
            pr_scope = "everything"
            """));
        Assert.Contains("pr_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Theme_Defaults_To_Dark_When_Absent()
    {
        var config = ConfigLoader.Parse(ValidToml);

        Assert.Equal(ThemeChoice.Dark, config.Theme);
    }

    [Theory]
    [InlineData("dark", ThemeChoice.Dark)]
    [InlineData("light", ThemeChoice.Light)]
    [InlineData("system", ThemeChoice.System)]
    [InlineData("Light", ThemeChoice.Light)]
    public void Theme_Is_Parsed_Case_Insensitively(string rawTheme, ThemeChoice expected)
    {
        var config = ConfigLoader.Parse(
            $"""
            theme = "{rawTheme}"

            [contexts.work]
            organization = "contoso"
            project = "P"
            """);

        Assert.Equal(expected, config.Theme);
    }

    [Fact]
    public void Theme_Value_Is_Trimmed()
    {
        var config = ConfigLoader.Parse(
            """
            theme = "  light  "

            [contexts.work]
            organization = "contoso"
            project = "P"
            """);

        Assert.Equal(ThemeChoice.Light, config.Theme);
    }

    [Fact]
    public void Theme_Key_Under_A_Context_Section_Is_An_Error()
    {
        // A `theme` line appended after a [contexts.*] header binds to that context in TOML;
        // it must fail loudly rather than be silently ignored (which would leave the app dark).
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(
            """
            default_context = "work"

            [contexts.work]
            organization = "contoso"
            project = "P"
            theme = "light"
            """));
        Assert.Contains("theme", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("top-level", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Theme_Invalid_Value_Is_An_Error()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(
            """
            theme = "blue"

            [contexts.work]
            organization = "contoso"
            project = "P"
            """));
        Assert.Contains("theme", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dark", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("light", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("system", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- [keys.<scope>] remap config (ticket #30) ----

    [Fact]
    public void Keys_Section_Absent_Yields_Empty_KeysConfig()
    {
        var config = ConfigLoader.Parse(ValidToml);

        Assert.Empty(config.Keys.Scopes);
    }

    [Fact]
    public void Parses_A_Single_String_Key_Override()
    {
        var config = ConfigLoader.Parse(
            $"""
            {ValidToml}

            [keys.global]
            move-down = "n"
            """);

        Assert.Equal(["n"], config.Keys.Scopes["global"]["move-down"]);
    }

    [Fact]
    public void Parses_An_Array_Of_Key_Sequences()
    {
        var config = ConfigLoader.Parse(
            $"""
            {ValidToml}

            [keys.global]
            move-down = ["n", "g j"]
            """);

        Assert.Equal(["n", "g j"], config.Keys.Scopes["global"]["move-down"]);
    }

    [Fact]
    public void Empty_String_Value_Parses_To_No_Sequences()
    {
        var config = ConfigLoader.Parse(
            $"""
            {ValidToml}

            [keys.global]
            move-down = ""
            """);

        Assert.Empty(config.Keys.Scopes["global"]["move-down"]);
    }

    [Fact]
    public void Keys_Section_Entry_Not_A_Table_Is_An_Error()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(
            $"""
            {ValidToml}

            [keys]
            global = "nope"
            """));
        Assert.Contains("keys.global", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Key_Value_Neither_String_Nor_Array_Is_An_Error()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(
            $"""
            {ValidToml}

            [keys.global]
            move-down = 5
            """));
        Assert.Contains("move-down", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
