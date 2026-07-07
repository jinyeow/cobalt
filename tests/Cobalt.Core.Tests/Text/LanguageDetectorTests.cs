using Cobalt.Core.Text.Syntax;

namespace Cobalt.Core.Tests.Text;

public class LanguageDetectorTests
{
    [Theory]
    [InlineData("/src/Foo.cs", Language.CSharp)]
    [InlineData("/src/app.ts", Language.JsTs)]
    [InlineData("/src/app.tsx", Language.JsTs)]
    [InlineData("/src/app.js", Language.JsTs)]
    [InlineData("/src/app.jsx", Language.JsTs)]
    [InlineData("/src/app.mjs", Language.JsTs)]
    [InlineData("/src/app.cjs", Language.JsTs)]
    [InlineData("/config.json", Language.Json)]
    [InlineData("/script.py", Language.Python)]
    [InlineData("/notes.txt", Language.None)]
    [InlineData("/README", Language.None)]
    [InlineData("/weird.", Language.None)]
    public void Detects_Language_From_Extension(string path, Language expected)
    {
        Assert.Equal(expected, LanguageDetector.FromPath(path));
    }

    [Fact]
    public void Detection_Is_Case_Insensitive()
    {
        Assert.Equal(Language.CSharp, LanguageDetector.FromPath("/SRC/FOO.CS"));
        Assert.Equal(Language.Json, LanguageDetector.FromPath("/Config.JSON"));
    }
}
