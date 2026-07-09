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
}
