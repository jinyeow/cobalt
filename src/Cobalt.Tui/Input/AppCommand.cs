namespace Cobalt.Tui.Input;

public enum AppCommand
{
    // navigation
    MoveDown,
    MoveUp,
    MoveLeft,
    MoveRight,
    MoveTop,
    MoveBottom,
    HalfPageDown,
    HalfPageUp,
    Open,
    Back,

    // app chrome
    Quit,
    Refresh,
    Help,
    CommandPalette,
    FilterStart,
    NextTab,
    PrevTab,
    FocusLeft,
    FocusRight,
    SectionWorkItems,
    SectionPullRequests,

    // shared verbs
    YankId,
    OpenInBrowser,
    Comment,
    EditInEditor,

    // work items
    ChangeState,
    Assign,
    EditTags,

    // pull requests
    Vote,
    ResolveThread,
    ReactivateThread,
    CompletePr,
    AbandonPr,
}
