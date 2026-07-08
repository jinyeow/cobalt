# Architecture Decision Records

Decisions that shape Cobalt, in the order they were made. Each record is
immutable once accepted; superseding decisions get a new number that links back.

| # | Title | Status |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-hand-rolled-rest-client.md) | Hand-rolled REST client over Azure DevOps REST API 7.2 | Accepted |
| [0003](0003-entra-id-only-auth.md) | Entra ID-only auth via Azure.Identity credential chain | Accepted |
| [0004](0004-terminal-gui-v2-with-viewmodels.md) | Terminal.Gui v2 behind a TUI-free view-model layer | Accepted |
| [0005](0005-dotnet-global-tool-packaging.md) | Distribute as a dotnet global tool | Accepted |
| [0006](0006-tdd-and-central-package-management.md) | TDD workflow, xunit v3, central package management | Accepted |
| [0007](0007-vim-input-as-testable-data.md) | Vim input as testable data, verified without a live terminal | Accepted |
| [0008](0008-client-side-diff-and-line-comments.md) | Client-side unified diff and line-comment anchoring | Accepted |
| [0009](0009-editor-suspend-resume.md) | Suspend Terminal.Gui by parking the UI thread around external processes | Accepted |
| [0010](0010-diff-pane-colored-listview-data-source.md) | Color the diff pane through a custom `IListDataSource`, styled by a pure run model | Accepted |
| [0011](0011-cross-project-org-pr-scope.md) | Cross-project (org-wide) PR scope, with per-PR-project drill-in | Accepted |
| [0012](0012-lazy-background-comment-counts.md) | Lazy, background per-PR comment counts | Accepted |
| [0013](0013-exception-handling-policy.md) | Narrow-at-operation exception handling with a global crash boundary | Accepted |
| [0014](0014-shared-count-aware-scroll-seam.md) | Shared count-aware scroll seam for lists and dialogs | Accepted |
| [0015](0015-team-pr-view.md) | Team PR tab (raw union of team-reviewed and teammate-authored PRs) | Accepted |
| [0016](0016-terminal-driver-selection.md) | Terminal.Gui driver selection under multiplexers (`COBALT_DRIVER`) | Accepted |
| [0017](0017-diff-review-ux-tree-sidebyside-responsive.md) | Diff-review UX: directory tree, side-by-side toggle, responsive panes | Accepted |
| [0018](0018-diff-review-as-a-complete-review-surface.md) | Diff review as a complete review surface (threads, nav, search, fold, stats, policy) | Accepted |
| [0019](0019-hybrid-theming.md) | Hybrid theming: Terminal.Gui themes for chrome, a cobalt palette for the diff | Accepted |
