using System.Text;
using Cobalt.Core.Config;

namespace Cobalt.Tui.Input;

/// <summary>
/// The vim layer as data: key-token sequences per scope (SPEC §5). Tokens are
/// single keys ("j", "G", ":"), control chords ("C-d"), or named keys ("Enter",
/// "Esc", "Tab", "S-Tab"). One table so a future remapping config has one seam.
/// </summary>
public sealed class KeyBindingTable
{
    private readonly Dictionary<KeyScope, List<(string[] Sequence, AppCommand Command)>> _bindings = [];

    // INPUT-2: Visible() is on the router's hot path (once per keystroke); cache its per-scope
    // result so a steady-state Feed() doesn't rebuild the combined (scoped + global) list every
    // time. Bind() clears the cache, so it stays correct even if bindings are added after an
    // earlier read (only Default()'s own construction does this in practice).
    private readonly Dictionary<KeyScope, (string[] Sequence, AppCommand Command)[]> _visibleCache = [];

    /// <summary>
    /// The one process-wide default table (INPUT-4): treated as immutable once built, so call
    /// sites that only ever want the default binding set can share it instead of each building
    /// (and re-validating) a fresh <see cref="Default"/> instance.
    /// </summary>
    public static KeyBindingTable Shared { get; } = Default();

    public static KeyBindingTable Default()
    {
        var table = new KeyBindingTable();

        table.Bind(KeyScope.Global, "j", AppCommand.MoveDown);
        table.Bind(KeyScope.Global, "Down", AppCommand.MoveDown);
        table.Bind(KeyScope.Global, "k", AppCommand.MoveUp);
        table.Bind(KeyScope.Global, "Up", AppCommand.MoveUp);
        table.Bind(KeyScope.Global, "g g", AppCommand.MoveTop);
        table.Bind(KeyScope.Global, "G", AppCommand.MoveBottom);
        table.Bind(KeyScope.Global, "C-d", AppCommand.HalfPageDown);
        table.Bind(KeyScope.Global, "C-u", AppCommand.HalfPageUp);
        table.Bind(KeyScope.Global, "Enter", AppCommand.Open);
        table.Bind(KeyScope.Global, "o", AppCommand.Open);
        table.Bind(KeyScope.Global, "l", AppCommand.Open); // vim "move into" opens the selection
        table.Bind(KeyScope.Global, "q", AppCommand.Back);
        table.Bind(KeyScope.Global, "h", AppCommand.Back); // vim "move out" goes back / closes
        table.Bind(KeyScope.Global, "r", AppCommand.Refresh);
        table.Bind(KeyScope.Global, "?", AppCommand.Help);
        table.Bind(KeyScope.Global, ":", AppCommand.CommandPalette);
        table.Bind(KeyScope.Global, "/", AppCommand.FilterStart);
        table.Bind(KeyScope.Global, "Tab", AppCommand.NextTab);
        table.Bind(KeyScope.Global, "S-Tab", AppCommand.PrevTab);
        table.Bind(KeyScope.Global, "C-h", AppCommand.FocusLeft);
        table.Bind(KeyScope.Global, "C-l", AppCommand.FocusRight);
        // Digits are reserved for count prefixes (e.g. 5j); sections move via g-chords.
        table.Bind(KeyScope.Global, "g t", AppCommand.NextSection);
        table.Bind(KeyScope.Global, "g T", AppCommand.PrevSection);
        table.Bind(KeyScope.Global, "g 1", AppCommand.SectionWorkItems);
        table.Bind(KeyScope.Global, "g 2", AppCommand.SectionPullRequests);
        table.Bind(KeyScope.Global, "y y", AppCommand.YankId);
        table.Bind(KeyScope.Global, "g x", AppCommand.OpenInBrowser);

        table.Bind(KeyScope.WorkItemList, "c", AppCommand.Comment);
        table.Bind(KeyScope.WorkItemList, "s", AppCommand.ChangeState);
        table.Bind(KeyScope.WorkItemList, "a", AppCommand.Assign);
        table.Bind(KeyScope.WorkItemList, "t", AppCommand.EditTags);
        // Scoped Tab shadows the global NextTab (the DiffReview mechanism below): in the
        // list+preview workspace Tab cycles pane focus (ADR 0024). While the preview is
        // hidden the shell falls back to today's Tab semantics.
        table.Bind(KeyScope.WorkItemList, "Tab", AppCommand.CyclePane);

        table.Bind(KeyScope.WorkItemDetail, "c", AppCommand.Comment);
        table.Bind(KeyScope.WorkItemDetail, "e", AppCommand.EditInEditor);
        table.Bind(KeyScope.WorkItemDetail, "s", AppCommand.ChangeState);
        table.Bind(KeyScope.WorkItemDetail, "a", AppCommand.Assign);
        table.Bind(KeyScope.WorkItemDetail, "t", AppCommand.EditTags);

        table.Bind(KeyScope.PullRequestList, "v", AppCommand.Vote);
        // lazygit's panel-tab keys: [ / ] are the canonical PR sub-tab keys (ADR 0021);
        // S-Tab remains the global PrevTab alias. The preview workspace has claimed Tab
        // for pane focus (ADR 0024) — the scoped binding below shadows the global
        // NextTab, and the shell falls back to today's Tab semantics while the preview
        // is hidden.
        table.Bind(KeyScope.PullRequestList, "]", AppCommand.NextTab);
        table.Bind(KeyScope.PullRequestList, "[", AppCommand.PrevTab);
        table.Bind(KeyScope.PullRequestList, "Tab", AppCommand.CyclePane);

        table.Bind(KeyScope.PullRequestDetail, "v", AppCommand.Vote);
        table.Bind(KeyScope.PullRequestDetail, "c", AppCommand.Comment);
        table.Bind(KeyScope.PullRequestDetail, "x", AppCommand.ResolveThread);
        table.Bind(KeyScope.PullRequestDetail, "u", AppCommand.ReactivateThread);
        table.Bind(KeyScope.PullRequestDetail, "d", AppCommand.OpenDiff);
        table.Bind(KeyScope.PullRequestDetail, "C", AppCommand.CompletePr);
        table.Bind(KeyScope.PullRequestDetail, "A", AppCommand.AbandonPr);
        table.Bind(KeyScope.PullRequestDetail, "g c", AppCommand.AddPrComment);
        table.Bind(KeyScope.PullRequestDetail, "g b", AppCommand.OpenBranch);

        table.Bind(KeyScope.DiffReview, "c", AppCommand.Comment);
        // Note: x/u (resolve/reactivate) belong to PR detail, NOT diff review —
        // DiffReviewDialog.Dispatch implements neither, so they are intentionally
        // unbound here (they would otherwise be advertised-but-dead keys, M3).
        table.Bind(KeyScope.DiffReview, "] f", AppCommand.NextFile);
        table.Bind(KeyScope.DiffReview, "[ f", AppCommand.PrevFile);
        table.Bind(KeyScope.DiffReview, "] c", AppCommand.NextHunk);
        table.Bind(KeyScope.DiffReview, "[ c", AppCommand.PrevHunk);
        table.Bind(KeyScope.DiffReview, "] t", AppCommand.NextThread);
        table.Bind(KeyScope.DiffReview, "[ t", AppCommand.PrevThread);
        table.Bind(KeyScope.DiffReview, "] v", AppCommand.NextUnviewedFile);
        table.Bind(KeyScope.DiffReview, "[ v", AppCommand.PrevUnviewedFile);
        table.Bind(KeyScope.DiffReview, "v", AppCommand.Vote);
        table.Bind(KeyScope.DiffReview, "/", AppCommand.SearchDiff);
        table.Bind(KeyScope.DiffReview, "n", AppCommand.SearchNext);
        table.Bind(KeyScope.DiffReview, "N", AppCommand.SearchPrev);
        table.Bind(KeyScope.DiffReview, "m", AppCommand.MarkViewed);
        table.Bind(KeyScope.DiffReview, "M", AppCommand.MarkUnviewed);
        table.Bind(KeyScope.DiffReview, "T", AppCommand.ToggleThreadFilter);
        table.Bind(KeyScope.DiffReview, "e", AppCommand.ExpandContext);
        table.Bind(KeyScope.DiffReview, "E", AppCommand.ExpandAllContext);
        // z collapses/expands the directory under the cursor in the file tree.
        table.Bind(KeyScope.DiffReview, "z", AppCommand.ToggleFold);
        // s toggles the diff pane between unified and side-by-side.
        table.Bind(KeyScope.DiffReview, "s", AppCommand.ToggleDiffMode);
        // Scoped Tab shadows the global NextTab: in diff review Tab cycles the two panes.
        table.Bind(KeyScope.DiffReview, "Tab", AppCommand.CyclePane);
        // Scoped h/l shadow the global Back/Open (same mechanism as scoped Tab above):
        // in diff review they scroll the focused pane horizontally instead.
        table.Bind(KeyScope.DiffReview, "h", AppCommand.ScrollLeft);
        table.Bind(KeyScope.DiffReview, "l", AppCommand.ScrollRight);
        table.Bind(KeyScope.DiffReview, "g b", AppCommand.OpenBranch);

        table.Bind(KeyScope.ThreadView, "c", AppCommand.Comment);
        table.Bind(KeyScope.ThreadView, "x", AppCommand.ResolveThread);
        table.Bind(KeyScope.ThreadView, "u", AppCommand.ReactivateThread);

        table.Validate();
        return table;
    }

    /// <summary>
    /// The default table with a user's <c>[keys.&lt;scope&gt;]</c> overrides applied (the
    /// single remapping seam ADR 0007 designed for). Per command: a config entry replaces
    /// that command's default bindings in the named scope (an empty sequence list unbinds
    /// it); a command with no config entry keeps its default bindings; a command bound only
    /// in config extends the table with a new binding. Fails loud (<see cref="ConfigException"/>)
    /// on an unknown scope/command name, and on a conflict the resulting table would produce
    /// (duplicate sequence or prefix shadowing within the same scope).
    /// </summary>
    public static KeyBindingTable FromConfig(KeysConfig config)
    {
        var overridesByScope = new Dictionary<KeyScope, IReadOnlyDictionary<string, IReadOnlyList<string>>>();
        foreach (var (scopeName, commands) in config.Scopes)
        {
            overridesByScope[ResolveScope(scopeName)] = commands;
        }

        var defaults = Default();
        var table = new KeyBindingTable();
        foreach (var scope in Enum.GetValues<KeyScope>())
        {
            var own = defaults._bindings.TryGetValue(scope, out var defaultList)
                ? new List<(string[] Sequence, AppCommand Command)>(defaultList)
                : [];

            if (overridesByScope.TryGetValue(scope, out var overrides))
            {
                var scopeLabel = scope.ToString().ToLowerInvariant();
                // Distinct config keys (TOML is case-sensitive) can still resolve to the
                // same AppCommand case-insensitively (e.g. "refresh" and "Refresh"); track
                // that so the second doesn't silently overwrite the first (own.RemoveAll +
                // re-add would otherwise make this a last-wins, no-error operation).
                var resolvedFrom = new Dictionary<AppCommand, string>();
                foreach (var (commandName, sequences) in overrides)
                {
                    var command = ResolveCommand(commandName);
                    if (resolvedFrom.TryGetValue(command, out var firstName))
                    {
                        throw new ConfigException(
                            $"[keys.{scopeLabel}] '{firstName}' and '{commandName}' both resolve to command " +
                            $"{command}; remove the duplicate");
                    }
                    resolvedFrom[command] = commandName;

                    own.RemoveAll(b => b.Command == command);
                    foreach (var sequence in sequences)
                    {
                        var tokens = sequence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        ValidateReachable(scopeLabel, commandName, sequence, tokens);
                        own.Add((tokens, command));
                    }
                }
            }

            foreach (var (sequence, command) in own)
            {
                table.Bind(scope, string.Join(' ', sequence), command);
            }
        }

        try
        {
            table.Validate();
        }
        catch (InvalidOperationException ex)
        {
            throw new ConfigException($"[keys] config produces a conflicting binding: {ex.Message}");
        }

        return table;
    }

    private static KeyScope ResolveScope(string name)
    {
        foreach (var scope in Enum.GetValues<KeyScope>())
        {
            if (string.Equals(scope.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                return scope;
            }
        }
        var valid = string.Join(", ", Enum.GetValues<KeyScope>().Select(s => s.ToString().ToLowerInvariant()));
        throw new ConfigException($"unknown key scope '[keys.{name}]'; valid scopes: {valid}");
    }

    private static AppCommand ResolveCommand(string name)
    {
        foreach (var command in Enum.GetValues<AppCommand>())
        {
            if (string.Equals(ToKebabCase(command), name, StringComparison.OrdinalIgnoreCase))
            {
                return command;
            }
        }
        throw new ConfigException($"unknown key-binding command '{name}'");
    }

    /// <summary>"MoveDown" -&gt; "move-down": the canonical config spelling for an <see cref="AppCommand"/>.</summary>
    private static string ToKebabCase(AppCommand command)
    {
        var name = command.ToString();
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                sb.Append('-');
            }
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Rejects a sequence <see cref="KeymapRouter"/> could never deliver as a binding:
    /// "Esc" always short-circuits to cancel (checked first, unconditionally), and a
    /// leading digit is always consumed as a count prefix (checked before any binding
    /// lookup) — so either makes the binding permanently dead.
    /// </summary>
    private static void ValidateReachable(string scopeLabel, string commandName, string rawSequence, string[] tokens)
    {
        if (tokens.Contains("Esc", StringComparer.Ordinal))
        {
            throw new ConfigException(
                $"[keys.{scopeLabel}] {commandName} = \"{rawSequence}\" contains \"Esc\", which the router " +
                "always treats as cancel; this binding could never fire");
        }
        if (tokens.Length > 0 && tokens[0].Length == 1 && tokens[0][0] is >= '0' and <= '9')
        {
            throw new ConfigException(
                $"[keys.{scopeLabel}] {commandName} = \"{rawSequence}\" starts with a digit, which the router " +
                "always consumes as a count prefix; this binding could never fire");
        }
        foreach (var token in tokens)
        {
            if (!IsEmittableToken(token))
            {
                throw new ConfigException(
                    $"[keys.{scopeLabel}] {commandName} = \"{rawSequence}\" contains \"{token}\", which no " +
                    "keypress produces — tokens are single keys (\"j\"), control chords (\"C-d\"), or the " +
                    "named keys Enter, Tab, S-Tab, Up, Down, separated by spaces (\"5j\" is the two keys \"5 j\")");
            }
        }
    }

    // The named tokens KeyTokenizer emits besides single runes and C-a..C-z chords ("Esc" is
    // rejected above before this list matters).
    private static readonly string[] NamedTokens = ["Enter", "Tab", "S-Tab", "Up", "Down"];

    /// <summary>
    /// True when <c>KeyTokenizer</c> can actually emit <paramref name="token"/> for some
    /// keypress: a single rune, a <c>C-a</c>..<c>C-z</c> chord, or a named key. Anything else
    /// (e.g. the two-character "5j") would sit in the table forever without ever matching.
    /// </summary>
    private static bool IsEmittableToken(string token)
    {
        if (NamedTokens.Contains(token))
        {
            return true;
        }
        if (token.Length == 3 && token.StartsWith("C-", StringComparison.Ordinal) && token[2] is >= 'a' and <= 'z')
        {
            return true;
        }
        // A single rune (one UTF-16 unit, or a surrogate pair for astral-plane characters) —
        // but only a printable one: the tokenizer never emits raw control runes (Ctrl-A is the
        // "C-a" chord; a TOML-escaped control char would be another permanently dead binding).
        return System.Text.Rune.TryGetRuneAt(token, 0, out var rune)
            && rune.Utf16SequenceLength == token.Length
            && !System.Text.Rune.IsControl(rune);
    }

    public void Bind(KeyScope scope, string sequence, AppCommand command)
    {
        if (!_bindings.TryGetValue(scope, out var list))
        {
            _bindings[scope] = list = [];
        }
        list.Add((sequence.Split(' ', StringSplitOptions.RemoveEmptyEntries), command));
        // A scoped or global bind can change what's visible from any scope (global bindings
        // fall through everywhere); clearing the whole cache is cheap since Bind only runs
        // during table construction.
        _visibleCache.Clear();
    }

    /// <summary>
    /// Guards the router's "exact match fires immediately" rule: no complete sequence
    /// may be a strict prefix of another in the same scope, or the longer one becomes
    /// unreachable. Cheap insurance for the future remapping seam (ADR 0007).
    /// </summary>
    public void Validate()
    {
        foreach (var scope in Enum.GetValues<KeyScope>())
        {
            // Duplicate-sequence check runs against the scope's OWN bindings only (not the
            // global fallback): a scoped binding deliberately reusing a global key to shadow
            // it (e.g. DiffReview's "h" over Global's "h") is by design (KeymapRouter picks
            // the first — scoped — match), not a conflict. Two different commands sharing an
            // exact sequence within the same own list, though, is unreachable/ambiguous.
            var own = scope == KeyScope.Global ? Visible(KeyScope.Global) : ScopedOnly(scope).ToArray();
            var seen = new Dictionary<string, AppCommand>();
            foreach (var (sequence, command) in own)
            {
                var key = string.Join(" ", sequence);
                if (seen.TryGetValue(key, out var existing))
                {
                    if (existing != command)
                    {
                        throw new InvalidOperationException(
                            $"key binding '{key}' is bound to both {existing} and {command} in scope {scope}");
                    }
                }
                else
                {
                    seen[key] = command;
                }
            }

            var sequences = Visible(scope).Select(b => b.Sequence).ToList();
            foreach (var a in sequences)
            {
                foreach (var b in sequences.Where(b => !ReferenceEquals(a, b) && a.Length < b.Length))
                {
                    if (a.SequenceEqual(b.Take(a.Length), StringComparer.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"key binding '{string.Join(" ", a)}' is a prefix of '{string.Join(" ", b)}' " +
                            $"in scope {scope}; the longer binding would be unreachable");
                    }
                }
            }
        }
    }

    /// <summary>The scope's own (non-global) bindings only — the verbs a modal in that scope actually dispatches.</summary>
    public IEnumerable<(string[] Sequence, AppCommand Command)> ScopedOnly(KeyScope scope) =>
        scope != KeyScope.Global && _bindings.TryGetValue(scope, out var scoped) ? scoped : [];

    /// <summary>
    /// All bindings visible from a scope: scoped first, then global fallback. Built once per
    /// scope and cached (INPUT-2) — see <see cref="Bind"/> for cache invalidation.
    /// </summary>
    public (string[] Sequence, AppCommand Command)[] Visible(KeyScope scope)
    {
        if (_visibleCache.TryGetValue(scope, out var cached))
        {
            return cached;
        }

        List<(string[] Sequence, AppCommand Command)> built = [];
        if (scope != KeyScope.Global && _bindings.TryGetValue(scope, out var scoped))
        {
            built.AddRange(scoped);
        }
        if (_bindings.TryGetValue(KeyScope.Global, out var global))
        {
            built.AddRange(global);
        }

        var array = built.ToArray();
        _visibleCache[scope] = array;
        return array;
    }
}
