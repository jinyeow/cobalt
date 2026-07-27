namespace Cobalt.Tui.Input;

/// <summary>Which screen is asking; scoped bindings win over global ones.</summary>
public enum KeyScope
{
    Global,
    WorkItemList,
    WorkItemDetail,
    PullRequestList,
    PullRequestDetail,
    DiffReview,
    ThreadView,
    // The popup menu (ADR 0022 stage C). It binds nothing of its own — everything it needs
    // (j/k, Enter/o/l, q/h, /) is Global — but the scope makes its routing menu-addressed and
    // gives a `[keys.menu]` remap somewhere to land.
    Menu,
}
