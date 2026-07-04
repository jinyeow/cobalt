namespace Cobalt.Tui.Input;

/// <summary>
/// The vim layer as data: key-token sequences per scope (SPEC §5). Tokens are
/// single keys ("j", "G", ":"), control chords ("C-d"), or named keys ("Enter",
/// "Esc", "Tab", "S-Tab"). One table so a future remapping config has one seam.
/// </summary>
public sealed class KeyBindingTable
{
    private readonly Dictionary<KeyScope, List<(string[] Sequence, AppCommand Command)>> _bindings = [];

    public static KeyBindingTable Default()
    {
        var table = new KeyBindingTable();

        table.Bind(KeyScope.Global, "j", AppCommand.MoveDown);
        table.Bind(KeyScope.Global, "Down", AppCommand.MoveDown);
        table.Bind(KeyScope.Global, "k", AppCommand.MoveUp);
        table.Bind(KeyScope.Global, "Up", AppCommand.MoveUp);
        table.Bind(KeyScope.Global, "h", AppCommand.MoveLeft);
        table.Bind(KeyScope.Global, "l", AppCommand.MoveRight);
        table.Bind(KeyScope.Global, "g g", AppCommand.MoveTop);
        table.Bind(KeyScope.Global, "G", AppCommand.MoveBottom);
        table.Bind(KeyScope.Global, "C-d", AppCommand.HalfPageDown);
        table.Bind(KeyScope.Global, "C-u", AppCommand.HalfPageUp);
        table.Bind(KeyScope.Global, "Enter", AppCommand.Open);
        table.Bind(KeyScope.Global, "o", AppCommand.Open);
        table.Bind(KeyScope.Global, "q", AppCommand.Back);
        table.Bind(KeyScope.Global, "r", AppCommand.Refresh);
        table.Bind(KeyScope.Global, "?", AppCommand.Help);
        table.Bind(KeyScope.Global, ":", AppCommand.CommandPalette);
        table.Bind(KeyScope.Global, "/", AppCommand.FilterStart);
        table.Bind(KeyScope.Global, "Tab", AppCommand.NextTab);
        table.Bind(KeyScope.Global, "S-Tab", AppCommand.PrevTab);
        table.Bind(KeyScope.Global, "C-h", AppCommand.FocusLeft);
        table.Bind(KeyScope.Global, "C-l", AppCommand.FocusRight);
        table.Bind(KeyScope.Global, "1", AppCommand.SectionWorkItems);
        table.Bind(KeyScope.Global, "2", AppCommand.SectionPullRequests);
        table.Bind(KeyScope.Global, "y y", AppCommand.YankId);
        table.Bind(KeyScope.Global, "g x", AppCommand.OpenInBrowser);

        table.Bind(KeyScope.WorkItemList, "c", AppCommand.Comment);
        table.Bind(KeyScope.WorkItemList, "s", AppCommand.ChangeState);
        table.Bind(KeyScope.WorkItemList, "a", AppCommand.Assign);

        table.Bind(KeyScope.WorkItemDetail, "c", AppCommand.Comment);
        table.Bind(KeyScope.WorkItemDetail, "e", AppCommand.EditInEditor);
        table.Bind(KeyScope.WorkItemDetail, "s", AppCommand.ChangeState);
        table.Bind(KeyScope.WorkItemDetail, "a", AppCommand.Assign);
        table.Bind(KeyScope.WorkItemDetail, "t", AppCommand.EditTags);

        table.Bind(KeyScope.PullRequestList, "v", AppCommand.Vote);

        table.Bind(KeyScope.PullRequestDetail, "v", AppCommand.Vote);
        table.Bind(KeyScope.PullRequestDetail, "c", AppCommand.Comment);
        table.Bind(KeyScope.PullRequestDetail, "x", AppCommand.ResolveThread);
        table.Bind(KeyScope.PullRequestDetail, "u", AppCommand.ReactivateThread);

        table.Bind(KeyScope.DiffReview, "c", AppCommand.Comment);
        table.Bind(KeyScope.DiffReview, "x", AppCommand.ResolveThread);
        table.Bind(KeyScope.DiffReview, "u", AppCommand.ReactivateThread);
        table.Bind(KeyScope.DiffReview, "v", AppCommand.Vote);

        table.Validate();
        return table;
    }

    public void Bind(KeyScope scope, string sequence, AppCommand command)
    {
        if (!_bindings.TryGetValue(scope, out var list))
        {
            _bindings[scope] = list = [];
        }
        list.Add((sequence.Split(' ', StringSplitOptions.RemoveEmptyEntries), command));
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
            var sequences = Visible(scope).Select(b => b.Sequence).ToList();
            foreach (var a in sequences)
            {
                foreach (var b in sequences)
                {
                    if (!ReferenceEquals(a, b) &&
                        a.Length < b.Length &&
                        a.SequenceEqual(b.Take(a.Length), StringComparer.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"key binding '{string.Join(" ", a)}' is a prefix of '{string.Join(" ", b)}' " +
                            $"in scope {scope}; the longer binding would be unreachable");
                    }
                }
            }
        }
    }

    /// <summary>All bindings visible from a scope: scoped first, then global fallback.</summary>
    public IEnumerable<(string[] Sequence, AppCommand Command)> Visible(KeyScope scope)
    {
        if (scope != KeyScope.Global && _bindings.TryGetValue(scope, out var scoped))
        {
            foreach (var b in scoped)
            {
                yield return b;
            }
        }
        if (_bindings.TryGetValue(KeyScope.Global, out var global))
        {
            foreach (var b in global)
            {
                yield return b;
            }
        }
    }
}
