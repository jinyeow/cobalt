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
        // The close callback needs the dialog Build is about to return, so it reads the variable
        // (assigned below, before the run loop that can invoke it) rather than a captured value.
        Dialog? dialog = null;
        void Close()
        {
            if (dialog is { } running)
            {
                app.RequestStop(running);
            }
        }

        using var built = Build(
            app, title, vm, bindings, onAccept: option => chosen = option, requestClose: Close, out _, out _);
        dialog = built;
        app.Run(built);
        return chosen;
    }

    /// <summary>
    /// Builds and wires the popup without starting the run loop, exposing the inner
    /// <see cref="ListView"/> and filter <see cref="TextField"/> so a headless view-level test
    /// can drive the whole grammar.
    /// </summary>
    internal static Dialog Build<T>(
        IApplication app, string title, MenuViewModel<T> vm, KeyBindingTable bindings,
        Action<MenuOption<T>> onAccept, Action requestClose, out ListView list, out TextField filterField)
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

        // A one-line filter bar overlaying the bottom row, hidden until '/'.
        var filter = new TextField
        {
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Visible = false,
        };
        filterField = filter;

        var lastWidth = -1;
        void Render()
        {
            // The raw width is the guard's memory (the clamp is a rendering detail): storing the
            // clamped value would never settle at a true width of 0, since 0 != 1 on every pass.
            lastWidth = rows.Viewport.Width;
            var width = Math.Max(1, lastWidth);
            // The list is the source of truth for the highlight bar, but SetSource nulls it, so
            // the view-model's clamped index is the restore target and the list mirrors back.
            ListRenderTail.Apply(rows, vm.VisibleOptions.Count, "no matches", () => vm.FormatRows(width), vm.SelectedIndex);
            vm.SelectedIndex = rows.SelectedItem ?? 0;
        }

        void Accept()
        {
            if (vm.Selected is { } selected)
            {
                onAccept(selected);
            }
            requestClose();
        }

        void HideFilter()
        {
            filter.Visible = false;
            vm.SetFilter("");
            Render();
            rows.SetFocus();
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
                    Accept();
                    return true;
                case AppCommand.Back:
                    requestClose();
                    return true;
                case AppCommand.FilterStart:
                    filter.Text = "";
                    filter.Visible = true;
                    filter.SetFocus();
                    return true;
                default:
                    return true; // a modal menu swallows every other matched global
            }
        }

        // Typing narrows the rows on every keystroke; the highlight returns to the top match, so
        // Enter straight after typing runs what the user is looking at.
        filter.TextChanged += (_, _) =>
        {
            vm.SetFilter(filter.Text ?? "");
            Render();
        };
        filter.Accepting += (_, e) =>
        {
            e.Handled = true; // stop the Dialog's default-accept from closing us first
            Accept();
        };
        filter.KeyDown += (_, key) =>
        {
            if (key.KeyCode == Terminal.Gui.Drivers.KeyCode.Esc)
            {
                // Esc in the field cancels the filter only; the menu itself needs a second Esc.
                key.Handled = true;
                HideFilter();
            }
        };

        var keys = new DialogKeyRouter(bindings, KeyScope.Menu, Dispatch, requestClose);
        // Subscribed on both the focused list and the dialog so the pending/count state is shared
        // across both delivery points (the ADR 0014 one-instance rule).
        rows.KeyDown += keys.HandleKey;
        // While the filter bar owns the keys the router must stand down: printable runes belong to
        // the field, and the control chords that still bubble to the Dialog (C-u, C-d) would
        // otherwise scroll the list underneath it (ADR 0014's search-bar guard).
        dialog.KeyDown += (sender, key) =>
        {
            if (!(filter.Visible && filter.HasFocus))
            {
                keys.HandleKey(sender, key);
            }
        };
        // Row widths depend on the LIST's viewport, which is only known once it has been laid out
        // and again after a terminal resize; subscribe to the same view the width is read from
        // (the list-screen pattern) — listening on the dialog fires before its children are laid
        // out, so the rows would render one pass behind and open blank. Re-render on a real width
        // change only.
        rows.ViewportChanged += (_, _) =>
        {
            if (rows.Viewport.Width != lastWidth)
            {
                Render();
            }
        };

        dialog.Add(rows, filter);
        Render();
        return dialog;
    }
}
