using Cobalt.Tui.App;
using Cobalt.Tui.Input;

namespace Cobalt.Tui.Screens;

/// <summary>
/// The modal dialogs' shared key-routing state machine (ADR 0007 / 0014): tokenize, feed the
/// scope's <see cref="KeymapRouter"/>, swallow an in-progress sequence, and mark the key handled
/// only when the dialog actually acted — so a matched-but-ignored command still falls through to
/// the widget's native behavior. Esc clears a pending count/sequence first and only closes when
/// nothing is pending (mirrors the shell's Esc handling, L5). Each dialog keeps just its own
/// <c>Dispatch</c> verb table. Lives in <c>Screens/</c> (not <c>ViewModels/</c>) because it sets
/// <c>Handled</c> on a Terminal.Gui <c>Key</c>, which <c>ViewModelPurityTests</c> forbids per ADR 0004.
/// </summary>
/// <remarks>
/// One instance per dialog, subscribed wherever that dialog needs it: three dialogs subscribe both
/// the body <c>TextView</c> and the dialog itself, so the count/pending state must be shared across
/// both delivery points. A second instance would fork it and break "5j" across a focus change.
/// </remarks>
internal sealed class DialogKeyRouter(
    KeyBindingTable bindings,
    KeyScope scope,
    Func<AppCommand, int?, bool> dispatch,
    Action requestClose)
{
    private readonly KeymapRouter _router = new(bindings);

    public void HandleKey(object? sender, Terminal.Gui.Input.Key key)
    {
        var token = KeyTokenizer.ToToken(key);
        if (token is null)
        {
            return;
        }
        var hadPending = _router.HasPending;
        var result = _router.Feed(token, scope);
        switch (result.Kind)
        {
            case KeyResultKind.Pending:
                key.Handled = true; // swallow an in-progress sequence (e.g. after 'g')
                break;
            case KeyResultKind.Matched when dispatch(result.Command, result.Count):
                key.Handled = true;
                break;
            case KeyResultKind.Matched:
                break; // matched but this dialog doesn't act — let native widget behavior run
            default:
                if (token == "Esc")
                {
                    key.Handled = true;
                    if (!hadPending)
                    {
                        requestClose();
                    }
                }
                break;
        }
    }
}
