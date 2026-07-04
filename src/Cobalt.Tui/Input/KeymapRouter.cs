namespace Cobalt.Tui.Input;

public enum KeyResultKind
{
    /// <summary>No binding consumed the key; pending buffer cleared.</summary>
    None,

    /// <summary>Key extends a multi-key sequence; wait for more input.</summary>
    Pending,

    /// <summary>A binding fired.</summary>
    Matched,
}

public readonly record struct KeyResult(KeyResultKind Kind, AppCommand Command)
{
    public static KeyResult None => new(KeyResultKind.None, default);
    public static KeyResult Pending => new(KeyResultKind.Pending, default);
    public static KeyResult Matched(AppCommand command) => new(KeyResultKind.Matched, command);
}

/// <summary>
/// Stateful vim-style key sequencer: feeds one key token at a time, tracks the
/// pending prefix (e.g. "g" awaiting "g"/"x"), and resolves against the binding
/// table for the active scope. Pure logic — no Terminal.Gui types (ADR 0004).
/// </summary>
public sealed class KeymapRouter(KeyBindingTable table)
{
    private readonly List<string> _pending = [];

    public KeyResult Feed(string keyToken, KeyScope scope)
    {
        if (keyToken == "Esc")
        {
            _pending.Clear();
            return KeyResult.None;
        }

        _pending.Add(keyToken);

        var exact = default(AppCommand?);
        var extendable = false;
        foreach (var (sequence, command) in table.Visible(scope))
        {
            if (sequence.Length < _pending.Count)
            {
                continue;
            }
            if (!sequence.Take(_pending.Count).SequenceEqual(_pending, StringComparer.Ordinal))
            {
                continue;
            }
            if (sequence.Length == _pending.Count)
            {
                exact ??= command; // scoped bindings enumerate first, so they win
            }
            else
            {
                extendable = true;
            }
        }

        // Vim disambiguation, simplified: an exact match fires immediately; our
        // default table never makes one binding a strict prefix of another.
        if (exact is { } matched)
        {
            _pending.Clear();
            return KeyResult.Matched(matched);
        }
        if (extendable)
        {
            return KeyResult.Pending;
        }

        _pending.Clear();
        return KeyResult.None;
    }

    public void Reset() => _pending.Clear();
}
