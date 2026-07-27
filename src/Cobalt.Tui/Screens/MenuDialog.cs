using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>
/// The reusable popup menu (ADR 0022 stage C): a modal list of executable rows driven by the
/// vim grammar — j/k with counts, gg/G, Enter/o/l to run the highlighted row, q/h/Esc to
/// dismiss. <see cref="Run"/> returns the chosen row and the host acts on it *after* the run
/// loop stops, so a verb that opens its own dialog never nests run loops (the palette and
/// PrActions pattern).
/// </summary>
/// <remarks>
/// Unlike the detail dialogs, <c>Dispatch</c> returns true for every matched command: a menu has
/// no native widget behaviour worth falling through to, and letting the shell's own `?` through
/// would open a menu on top of a menu.
/// </remarks>
internal static class MenuDialog
{
    /// <summary>Runs the menu modally and returns the chosen row, or null if it was dismissed.</summary>
    public static MenuOption<T>? Run<T>(
        IApplication app, string title, IReadOnlyList<MenuOption<T>> options, KeyBindingTable bindings)
    {
        var vm = new MenuViewModel<T>(options);
        MenuOption<T>? chosen = null;
        Dialog? dialog = null;
        using var built = Build(
            app, title, vm, bindings,
            onAccept: option => chosen = option,
            requestClose: () => app.RequestStop(dialog!),
            out _);
        dialog = built;
        app.Run(built);
        return chosen;
    }

    /// <summary>
    /// Builds and wires the popup without starting the run loop, exposing the inner
    /// <see cref="ListView"/> so a headless view-level test can drive the whole grammar.
    /// </summary>
    internal static Dialog Build<T>(
        IApplication app, string title, MenuViewModel<T> vm, KeyBindingTable bindings,
        Action<MenuOption<T>> onAccept, Action requestClose, out ListView list)
    {
        var dialog = new Dialog
        {
            Title = $"{title} — Enter to run, / to filter, q to close",
            Width = Dim.Percent(70),
            Height = Dim.Percent(70),
        };
        var rows = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true,
        };
        // Vim command keys drive this list, so disable ListView type-ahead — otherwise the
        // CollectionNavigator swallows the menu's letters before the router sees them
        // (the PrListView lesson).
        rows.KeystrokeNavigator = null;
        list = rows;

        var lastWidth = -1;
        void Render()
        {
            var width = Math.Max(1, rows.Viewport.Width);
            lastWidth = width;
            // The list is the source of truth for the highlight bar, but SetSource nulls it, so
            // the view-model's clamped index is the restore target and the list mirrors back.
            ListRenderTail.Apply(rows, vm.VisibleOptions.Count, "no matches", () => vm.FormatRows(width), vm.SelectedIndex);
            vm.SelectedIndex = rows.SelectedItem ?? 0;
        }

        bool Dispatch(AppCommand command, int? count)
        {
            if (VimScroll.Applies(command))
            {
                VimScroll.Apply(rows, command, count);
                vm.SelectedIndex = rows.SelectedItem ?? 0;
                return true;
            }
            switch (command)
            {
                case AppCommand.Open:
                    if (vm.Selected is { } selected)
                    {
                        onAccept(selected);
                    }
                    requestClose();
                    return true;
                case AppCommand.Back:
                    requestClose();
                    return true;
                default:
                    return true; // a modal menu swallows every other matched global
            }
        }

        var keys = new DialogKeyRouter(bindings, KeyScope.Menu, Dispatch, requestClose);
        // Subscribed on both the focused list and the dialog so the pending/count state is shared
        // across both delivery points (the ADR 0014 one-instance rule).
        rows.KeyDown += keys.HandleKey;
        dialog.KeyDown += keys.HandleKey;
        // Row widths depend on the viewport, which is only known once the dialog is laid out and
        // again after a terminal resize; re-render on a real width change only.
        dialog.ViewportChanged += (_, _) =>
        {
            if (rows.Viewport.Width != lastWidth)
            {
                Render();
            }
        };

        dialog.Add(rows);
        Render();
        return dialog;
    }
}
