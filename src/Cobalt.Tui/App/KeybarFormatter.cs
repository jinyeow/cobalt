using System.Text;
using Cobalt.Tui.Input;

namespace Cobalt.Tui.App;

/// <summary>
/// The always-visible bottom keybar (lazygit's bottom line): the highest-value keys
/// for the focused scope, generated from the live binding table so it can never
/// drift from behaviour. Pure string building — the shell only assigns the result.
/// </summary>
public static class KeybarFormatter
{
    // Curated order: contextual verbs first (the lazygit lesson — the bar teaches
    // what you can do to the selected thing), then navigation extras. Commands not
    // listed here still render, after these, described via the shared help
    // vocabulary, so new bindings surface without touching the formatter.
    // MoveDown/MoveUp are not listed: the movement pair is emitted first, by hand,
    // as a single "j/k:move" entry in Render.
    private static readonly (AppCommand Command, string Label)[] Priority =
    [
        (AppCommand.Open, "open"),
        (AppCommand.Comment, "comment"),
        (AppCommand.ChangeState, "state"),
        (AppCommand.Assign, "assign"),
        (AppCommand.EditTags, "tags"),
        (AppCommand.Vote, "vote"),
        (AppCommand.OpenDiff, "diff"),
        (AppCommand.NextTab, "tab"),
        (AppCommand.FilterStart, "filter"),
        (AppCommand.CommandPalette, "cmd"),
        (AppCommand.Refresh, "refresh"),
        (AppCommand.YankId, "yank"),
        (AppCommand.OpenInBrowser, "browser"),
        (AppCommand.NextSection, "section"),
        (AppCommand.Back, "quit"),
    ];

    // Aliases, reverse directions, and pure-scroll keys that would only repeat an
    // entry already on the bar (j/k implies C-d/C-u etc.). Help is suppressed here
    // because it is appended manually as the guaranteed last entry.
    private static readonly HashSet<AppCommand> Suppressed =
    [
        AppCommand.MoveUp, AppCommand.MoveTop, AppCommand.MoveBottom,
        AppCommand.HalfPageDown, AppCommand.HalfPageUp,
        AppCommand.PrevTab, AppCommand.PrevSection,
        AppCommand.SectionWorkItems, AppCommand.SectionPullRequests,
        AppCommand.FocusLeft, AppCommand.FocusRight,
        AppCommand.Help,
    ];

    public static string Render(KeyBindingTable table, KeyScope scope, int width)
    {
        // Among a command's aliases keep the densest key (Enter/o/l all open — the
        // bar shows "o"); the bar trades the help overlay's completeness for width.
        var first = new Dictionary<AppCommand, string>();
        foreach (var (sequence, command) in table.Visible(scope))
        {
            var display = string.Join("", sequence);
            if (!first.TryGetValue(command, out var existing) || display.Length < existing.Length)
            {
                first[command] = display;
            }
        }

        var entries = new List<string>();
        // Seed MoveDown so the fallback loop can't re-emit it; MoveUp stays out of
        // the bar via Suppressed (its direction is implied by the pair entry).
        var emitted = new HashSet<AppCommand> { AppCommand.MoveDown };
        if (first.TryGetValue(AppCommand.MoveDown, out var down))
        {
            entries.Add(first.TryGetValue(AppCommand.MoveUp, out var up)
                ? $"{down}/{up}:move"
                : $"{down}:move");
        }
        foreach (var (command, label) in Priority)
        {
            if (first.TryGetValue(command, out var key) && emitted.Add(command))
            {
                entries.Add($"{key}:{label}");
            }
        }
        foreach (var (command, key) in first)
        {
            if (!Suppressed.Contains(command) && emitted.Add(command))
            {
                entries.Add($"{key}:{HelpText.Describe(command)}");
            }
        }

        var help = first.TryGetValue(AppCommand.Help, out var helpKey) ? $"{helpKey}:help" : null;
        return Fit(entries, help, width);
    }

    private static string Fit(IReadOnlyList<string> entries, string? help, int width)
    {
        const string Sep = "  ";
        var sb = new StringBuilder(" ");
        var reserve = help is null ? 0 : Sep.Length + help.Length;
        foreach (var entry in entries)
        {
            var addition = (sb.Length > 1 ? Sep.Length : 0) + entry.Length;
            if (sb.Length + addition + reserve > width)
            {
                break;
            }
            if (sb.Length > 1)
            {
                sb.Append(Sep);
            }
            sb.Append(entry);
        }
        if (help is not null)
        {
            if (sb.Length > 1)
            {
                sb.Append(Sep);
            }
            sb.Append(help);
        }
        var bar = sb.ToString();
        // Clamp (not a raw slice) so an overflow can never split a surrogate pair.
        return bar.Length <= width ? bar : Screens.RowText.Clamp(bar, width);
    }
}
