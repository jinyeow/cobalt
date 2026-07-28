namespace Cobalt.Tui.ViewModels;

/// <summary>
/// One executable menu row (ADR 0022 stage C): what it says, the key that would do the same
/// thing directly, and the value the host acts on when the row is chosen. <typeparamref name="T"/>
/// is the verb domain — <c>AppCommand</c> for the `?` help menu, a vote or state value for the
/// verb menus. The row carries a value rather than a delegate so builders stay pure and the
/// host keeps the "view-model decides, shell performs" split (ADR 0007).
/// </summary>
public sealed record MenuOption<T>(string Label, string KeyHint, T Value);
