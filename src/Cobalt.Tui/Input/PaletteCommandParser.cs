namespace Cobalt.Tui.Input;

public enum PaletteActionKind
{
    None,
    Quit,
    Help,
    Messages,
    Log,
    SwitchContext,
    PickContext,
    SetScope,
    ToggleDone,
    SetProjectFilter,
    SetTheme,
    Unknown,
}

/// <summary>The kind of value a palette command's argument names, for Tab-completion in the palette.</summary>
public enum PaletteArgKind
{
    /// <summary>No structured argument provider — the raw text (if any) passes straight through.</summary>
    None,

    /// <summary>Argument completes against known context names.</summary>
    Context,

    /// <summary>Argument completes against known project names.</summary>
    Project,
}

/// <summary>One palette command's name, aliases, argument kind, and resulting action — the single
/// source both <see cref="PaletteCommandParser.Parse"/> and completion consume.</summary>
public readonly record struct PaletteCommandCatalogEntry(
    string Name, IReadOnlyList<string> Aliases, PaletteArgKind ArgKind, PaletteActionKind ActionKind);

public readonly record struct PaletteAction(PaletteActionKind Kind, string Argument = "");

/// <summary>Parses `:` command-palette input (`q`, `ctx NAME`, `scope`, `done`, `project`, `theme`, `help`, `messages`, `log`).</summary>
public static class PaletteCommandParser
{
    /// <summary>The full command vocabulary — one source for parsing and Tab-completion.</summary>
    public static readonly IReadOnlyList<PaletteCommandCatalogEntry> Catalog =
    [
        new("quit", ["q"], PaletteArgKind.None, PaletteActionKind.Quit),
        new("help", [], PaletteArgKind.None, PaletteActionKind.Help),
        new("messages", [], PaletteArgKind.None, PaletteActionKind.Messages),
        new("log", [], PaletteArgKind.None, PaletteActionKind.Log),
        new("context", ["ctx"], PaletteArgKind.Context, PaletteActionKind.SwitchContext),
        new("scope", [], PaletteArgKind.None, PaletteActionKind.SetScope),
        new("done", [], PaletteArgKind.None, PaletteActionKind.ToggleDone),
        new("project", [], PaletteArgKind.Project, PaletteActionKind.SetProjectFilter),
        new("theme", [], PaletteArgKind.None, PaletteActionKind.SetTheme),
    ];

    public static PaletteAction Parse(string input)
    {
        var trimmed = input.Trim().TrimStart(':').Trim();
        if (trimmed.Length == 0)
        {
            return new PaletteAction(PaletteActionKind.None);
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var word = parts[0];
        var argument = parts.Length > 1 ? parts[1] : null;

        foreach (var entry in Catalog)
        {
            if (entry.Name != word && !entry.Aliases.Contains(word))
            {
                continue;
            }
            return entry.ActionKind switch
            {
                PaletteActionKind.SwitchContext when argument is null => new PaletteAction(PaletteActionKind.PickContext),
                PaletteActionKind.SwitchContext => new PaletteAction(PaletteActionKind.SwitchContext, argument),
                PaletteActionKind.SetScope or PaletteActionKind.ToggleDone
                    or PaletteActionKind.SetProjectFilter or PaletteActionKind.SetTheme =>
                    new PaletteAction(entry.ActionKind, argument ?? ""),
                _ => new PaletteAction(entry.ActionKind),
            };
        }

        return new PaletteAction(PaletteActionKind.Unknown, $"unknown command: {trimmed}");
    }
}
