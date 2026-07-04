namespace Cobalt.Tui.Input;

public enum PaletteActionKind
{
    None,
    Quit,
    Help,
    Messages,
    SwitchContext,
    PickContext,
    Unknown,
}

public readonly record struct PaletteAction(PaletteActionKind Kind, string Argument = "");

/// <summary>Parses `:` command-palette input (`q`, `ctx NAME`, `help`, `messages`).</summary>
public static class PaletteCommandParser
{
    public static PaletteAction Parse(string input)
    {
        var trimmed = input.Trim().TrimStart(':').Trim();
        if (trimmed.Length == 0)
        {
            return new PaletteAction(PaletteActionKind.None);
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (parts[0], parts.Length > 1 ? parts[1] : null) switch
        {
            ("q" or "quit", _) => new PaletteAction(PaletteActionKind.Quit),
            ("help", _) => new PaletteAction(PaletteActionKind.Help),
            ("messages", _) => new PaletteAction(PaletteActionKind.Messages),
            ("ctx" or "context", null) => new PaletteAction(PaletteActionKind.PickContext),
            ("ctx" or "context", var name) => new PaletteAction(PaletteActionKind.SwitchContext, name),
            _ => new PaletteAction(PaletteActionKind.Unknown, $"unknown command: {trimmed}"),
        };
    }
}
