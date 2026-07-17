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

        table.Bind(KeyScope.WorkItemDetail, "c", AppCommand.Comment);
        table.Bind(KeyScope.WorkItemDetail, "e", AppCommand.EditInEditor);
        table.Bind(KeyScope.WorkItemDetail, "s", AppCommand.ChangeState);
        table.Bind(KeyScope.WorkItemDetail, "a", AppCommand.Assign);
        table.Bind(KeyScope.WorkItemDetail, "t", AppCommand.EditTags);

        table.Bind(KeyScope.PullRequestList, "v", AppCommand.Vote);
        // lazygit's panel-tab keys: [ / ] cycle the PR sub-tabs (review queue / team /
        // mine / active). Tab / S-Tab stay as aliases until the preview workspace
        // claims Tab for pane focus (spec: lazygit-inspired redesign).
        table.Bind(KeyScope.PullRequestList, "]", AppCommand.NextTab);
        table.Bind(KeyScope.PullRequestList, "[", AppCommand.PrevTab);

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
