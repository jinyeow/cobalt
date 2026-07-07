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
    NextSection,
    PrevSection,

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
    OpenDiff,

    // diff review
    NextFile,
    PrevFile,
    CyclePane,
    ToggleFold,
    ToggleDiffMode,
}
