namespace Cobalt.Core.Text.Syntax;

public enum Language
{
    None,
    CSharp,
    JsTs,
    Json,
    Python,
}

/// <summary>Maps a file path to a syntax <see cref="Language"/> by extension (case-insensitive).</summary>
public static class LanguageDetector
{
    public static Language FromPath(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ext.ToLowerInvariant() switch
        {
            ".cs" => Language.CSharp,
            ".js" or ".jsx" or ".mjs" or ".cjs" or ".ts" or ".tsx" => Language.JsTs,
            ".json" => Language.Json,
            ".py" => Language.Python,
            _ => Language.None,
        };
    }
}
