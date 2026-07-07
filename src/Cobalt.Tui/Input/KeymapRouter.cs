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

public readonly record struct KeyResult(KeyResultKind Kind, AppCommand Command, int? Count = null)
{
    public static KeyResult None => new(KeyResultKind.None, default);
    public static KeyResult Pending => new(KeyResultKind.Pending, default);
    public static KeyResult Matched(AppCommand command, int? count = null) => new(KeyResultKind.Matched, command, count);
}

/// <summary>
/// Stateful vim-style key sequencer: feeds one key token at a time, tracks the
/// pending prefix (e.g. "g" awaiting "g"/"x"), and resolves against the binding
/// table for the active scope. Pure logic — no Terminal.Gui types (ADR 0004).
/// </summary>
public sealed class KeymapRouter(KeyBindingTable table)
{
    private readonly List<string> _pending = [];

    // Accumulated numeric count prefix (vim "5j"). 0 means "no count".
    private int _count;

    /// <summary>The binding table this router resolves against (shared, so a dialog needn't build a second one).</summary>
    public KeyBindingTable Table => table;

    /// <summary>True when a count digit or a multi-key sequence is mid-entry — so Esc should clear it, not act.</summary>
    public bool HasPending => _pending.Count > 0 || _count > 0;

    public KeyResult Feed(string keyToken, KeyScope scope)
    {
        if (keyToken == "Esc")
        {
            _pending.Clear();
            _count = 0;
            return KeyResult.None;
        }

        // A leading run of digits (with no multi-key sequence pending) is a count
        // prefix, not a binding: "5j" moves down five rows. A bare "0" (no count yet)
        // is ignored — there is no line-start motion — so it can't be read as a count;
        // "0" only extends an already-started count (e.g. "10j").
        if (_pending.Count == 0 && keyToken.Length == 1 && keyToken[0] is >= '0' and <= '9')
        {
            var digit = keyToken[0] - '0';
            if (digit == 0 && _count == 0)
            {
                return KeyResult.None;
            }
            _count = Math.Min((_count * 10) + digit, 9999);
            return KeyResult.Pending;
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
            var count = _count == 0 ? (int?)null : _count;
            _count = 0;
            return KeyResult.Matched(matched, count);
        }
        if (extendable)
        {
            return KeyResult.Pending;
        }

        _pending.Clear();
        _count = 0;
        return KeyResult.None;
    }

    public void Reset()
    {
        _pending.Clear();
        _count = 0;
    }
}
