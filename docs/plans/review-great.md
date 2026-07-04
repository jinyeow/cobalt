# Plan: make PR review great — intra-line diff, syntax color, rename-aware diffs

Goal: the diff pane highlights the exact changed words on changed lines, colors
code tokens by language, and renames diff old-path→new-path instead of
rendering as a whole-file add. Logic is unit-tested in Core per ADR 0004/0007;
only a thin `IListDataSource` adapter is PTY-verified.

Scope decision (recommendation, not a hedge): ship **all three** parts in this
batch, with syntax highlighting cut to a **line-local tokenizer for C#, JS/TS,
JSON, Python + a plain fallback**. The rendering machinery (colored runs in the
diff pane) is required by intra-line highlighting anyway, so syntax color's
marginal cost is only a pure, easily-tested tokenizer — and Terminal.Gui
2.4.16's theme already ships `VisualRole.Code*` attributes, so we get
theme-consistent token colors for free. Deferred (fast follow, listed at the
end): cross-line constructs (block comments, triple-quoted strings), more
languages.

## Probe evidence (verified against Terminal.Gui 2.4.16 — do not re-derive)

Reflection over `terminal.gui/2.4.16/lib/net10.0/Terminal.Gui.dll` plus a live
PTY probe (scratchpad `lvprobe/` + `ptyterm.py` + pyte, cell-level color
assertions):

- `Terminal.Gui.Views.IListDataSource` has
  `void Render(ListView listView, bool selected, int item, int col, int row, int width, int viewportX)`
  plus `Count`, `MaxItemLength`, `SuspendCollectionChangedEvent`,
  `CollectionChanged`, `IsMarked/SetMark/RenderMark/ToList`, and is
  `IDisposable`. The built-in impl is `Terminal.Gui.Views.ListWrapper<T>`.
- `View` (base of `ListView`) exposes `bool Move(int col, int row)`,
  `Attribute SetAttribute(Attribute)`, `void AddStr(string)`,
  `void AddRune(Rune)`, `Attribute GetAttributeForRole(VisualRole)`.
- **Live PTY proof**: a custom `IListDataSource.Render` that calls
  `listView.Move(col,row)` then alternates `SetAttribute(...)` + `AddStr(...)`
  produced per-cell colored runs within one ListView row (pyte cell dump:
  `fg=000000 bg=008000` for one run, `fg=ffffff bg=ff0000` for the next,
  `fg=569cd6` for `GetAttributeForRole(VisualRole.CodeKeyword)`), while
  ListView selection/scrolling kept working. This is **Option B** and it is
  real.
- `VisualRole` includes semantic code roles supplied by the theme: `Code`,
  `CodeComment`, `CodeKeyword`, `CodeString`, `CodeNumber`, `CodeOperator`,
  `CodeType`, `CodePreprocessor`, `CodeIdentifier`, `CodeConstant`,
  `CodePunctuation`, `CodeFunctionName`, `CodeAttribute` — plus `Normal`,
  `Focus`, `Highlight`, etc.
- `Terminal.Gui.Drawing.Attribute` is a record with `Color Foreground`,
  `Color Background`, `TextStyle Style`; ctors include `(Color, Color)` and
  `(Color, Color, TextStyle)`. `Color` is a struct with RGB ctors
  (`(int,int,int,int)`, `(uint)`, `"#rrggbb"` string) and `ColorName16`
  (16-color ANSI names). `TextStyle` has `Bold/Italic/Underline/Reverse/...`.
- Option C (markup-in-string) **does not exist** in v2. The closest is
  `ListView.RowRender` event with `ListViewRowEventArgs { Row, RowAttribute }`
  — whole-row attribute only, cannot color runs. Rejected.
- Option A (custom View) is unnecessary: it would re-implement what ListView
  already gives us (`MoveUp/MoveDown/MovePageUp/MovePageDown/MoveHome/MoveEnd`,
  `SelectedItem`, `EnsureSelectedItemVisible`, viewport scrolling, focus).

**Decision: Option B** — keep the diff pane a `ListView`, replace its source
with a custom `IListDataSource` that draws colored runs. Nothing else in
`DiffReviewDialog` changes: `SelectedItem` get/set (comment-on-line +
selection preservation across refresh), `[`/`]` file nav, `c`, `Tab`, `q` are
all untouched. Note found while probing: `j`/`k` are bound only in the main
shell's `KeymapRouter` (`KeyBindingTable` Global scope) — modal dialogs never
see that router, so today the diff pane moves with arrow keys only. Task 8
adds a two-line `j`/`k` forward in the dialog's `KeyDown` since vim feel is
the product's core (cheap, in scope).

DiffPlex 1.9.0 (already referenced) supports sub-line diffing:
`IDiffer.CreateDiffs(string, string, bool, bool, IChunker)` with
`DiffPlex.Chunkers.WordChunker` / `CharacterChunker`; verified that
`WordChunker` pieces include whitespace tokens (`var| |total| |=| |price|...`)
and `DiffResult.DiffBlocks` gives `DeleteStartA/DeleteCountA/InsertStartB/InsertCountB`
piece indices, so char offsets are prefix sums of piece lengths.

ADO rename data (Microsoft Learn, `GitPullRequestChange` extends `GitChange`):
change entries carry `sourceServerItem` ("path of the item on the server" —
the OLD path for rename/move) and `originalPath` ("original path of item if
different from current path"). Our `PrChangeEntryDto` currently drops both.

## Design

### 1. Span model (Core, pure, unit-tested)

```csharp
// src/Cobalt.Core/Text/DiffService.cs (extended)
public sealed record LineSpan(int Start, int Length);   // char range in DiffLine.Text

public sealed record DiffLine(
    DiffLineKind Kind, int? OldLineNumber, int? NewLineNumber, string Text,
    IReadOnlyList<LineSpan>? ChangedSpans = null);      // null/[] = no intra-line info
```

The optional positional parameter keeps every existing `DiffLine(...)`
construction (tests, VM) compiling unchanged.

- `IntraLineDiff.Compute(string oldLine, string newLine)` →
  `(IReadOnlyList<LineSpan> OldSpans, IReadOnlyList<LineSpan> NewSpans)`.
  Word-level via `new Differ().CreateDiffs(oldLine, newLine, false, false,
  new WordChunker())`; map piece-index blocks to char spans by prefix sums;
  merge adjacent/touching spans. **Similarity guard**: if the changed chars on
  either side exceed 60% of that side's length, return empty spans — a total
  rewrite must render as plain add/remove, not confetti.
- Pairing pass inside `DiffService.Unified` (post-processing the line list —
  pure): scan for each maximal run of `Removed` lines immediately followed by
  a run of `Added` lines; pair k-th removed with k-th added
  (`min(removedCount, addedCount)` pairs, leftovers unpaired); fill
  `ChangedSpans` on both sides from `IntraLineDiff.Compute`.

### 2. Syntax tokenizer (Core, pure, unit-tested)

```csharp
// src/Cobalt.Core/Text/Syntax/*.cs
public enum Language { None, CSharp, JsTs, Json, Python }
public static class LanguageDetector { public static Language FromPath(string path); }

public enum TokenKind { Plain, Keyword, Identifier, Number, String, Comment, Operator, Punctuation }
public sealed record SyntaxToken(int Start, int Length, TokenKind Kind);
public static class SyntaxTokenizer
{
    public static IReadOnlyList<SyntaxToken> Tokenize(string line, Language language);
}
```

One hand-rolled single-pass scanner parameterized by a per-language spec
(keyword set; line-comment prefix `//` or `#`; quote chars `"` `'` `` ` ``
with `\` escapes; numeric literals incl. `0x`, `_`, decimals). C# and JS/TS
share the engine with different keyword sets; JSON is strings + numbers +
`true/false/null` + punctuation; `Language.None` returns
`[new SyntaxToken(0, line.Length, TokenKind.Plain)]`. Detection:
`.cs`→CSharp; `.js/.jsx/.mjs/.cjs/.ts/.tsx`→JsTs; `.json`→Json; `.py`→Python;
everything else→None (case-insensitive).

**Deliberate cut — strictly line-local.** No state carries across lines, so
the interior of a `/* ... */` block comment, a C# raw/verbatim string, or a
Python triple-quoted string spanning lines will tokenize as code. Correct
cross-line state would need *two* state streams threaded through interleaved
diff lines (old-side state through Context+Removed, new-side through
Context+Added) — real complexity for a cosmetic edge. Documented limitation;
fast follow if it grates.

### 3. Composition: one pure styler produces the display string + runs

```csharp
// src/Cobalt.Core/Text/DiffLineStyler.cs
public sealed record RunStyle(TokenKind Token, DiffLineKind LineKind, bool Emphasis, bool IsGutter);
public sealed record StyledRun(int Start, int Length, RunStyle Style);
public sealed record StyledLine(string DisplayText, IReadOnlyList<StyledRun> Runs);

public static class DiffLineStyler
{
    public static StyledLine Compose(DiffLine line, IReadOnlyList<SyntaxToken> tokens, bool hasThread);
}
```

`DisplayText` is exactly today's `FormatLine` output:
`{marker}{oldNo,4} {newNo,4} {sign}{line.Text}` (marker `●`/space) — asserted
in tests as a rendering-compat regression. Runs partition `DisplayText`
exactly (no gaps, no overlaps, ordered): gutter chars are one-or-more runs with
`IsGutter=true` (the sign char gets `Token=Plain, IsGutter=true` but carries
`LineKind` so the adapter can color `+`/`-`); code runs are the tokens offset
by the gutter width, gap-filled with `Plain`, **split at `ChangedSpans`
boundaries** with `Emphasis=true` inside a span.

**Layering rule (who wins on overlap): they layer, not fight.** Foreground
comes from the syntax token; background comes from the diff state
(`LineKind` + `Emphasis`). A keyword inside a changed word keeps its keyword
foreground and gains the emphasis background. This is decided in the pure
compositor (runs are split at every boundary); the adapter maps a `RunStyle`
to exactly one `Attribute` with no logic.

### 4. Rendering adapter (TUI, PTY-verified only)

`src/Cobalt.Tui/Screens/DiffListDataSource.cs` — `IListDataSource` over a
precomputed `IReadOnlyList<StyledLine>` (composed once per `Render()` pass in
the dialog, not per draw):

- `Render(...)`: `listView.Move(col, row)`; for each `StyledRun` (sliced by
  `viewportX` chars from the left): `listView.SetAttribute(Map(style, selected))`,
  `listView.AddStr(slice)`; pad to `width` with spaces in the line-kind
  background (full-width add/remove tint, GitHub-style).
- **Selected row**: render the whole row with `GetAttributeForRole(VisualRole.Focus)`
  and skip run colors — mirrors `ListWrapper` and keeps the selection bar
  unmistakable.
- `Count` = lines; `MaxItemLength` = max `DisplayText` length (keeps
  horizontal scrolling working); `IsMarked` false, `RenderMark` false,
  `SetMark` no-op, `CollectionChanged` no-op event (source is replaced
  wholesale on refresh, same as today's `SetSource`); `SuspendCollectionChangedEvent`
  auto-property; `Dispose` no-op.

Attribute mapping (`Map`), all inside the data source:

- Foreground by `TokenKind` via theme roles: Keyword→`CodeKeyword`,
  String→`CodeString`, Comment→`CodeComment`, Number→`CodeNumber`,
  Operator→`CodeOperator`, Punctuation→`CodePunctuation`,
  Identifier→`CodeIdentifier`, Plain→`Normal` — via
  `listView.GetAttributeForRole(role).Foreground`. Theme-aware and
  terminal-safe by construction.
- Background by diff state: Context → `Normal` background; Added →
  `new Color("#123a12")`; Removed → `new Color("#3a1212")`; Emphasis-Added →
  `new Color("#1e6b1e")`; Emphasis-Removed → `new Color("#6b2020")`.
  Gutter sign run: fg `ColorName16.BrightGreen`/`BrightRed` on Normal bg.
  Degradation: Terminal.Gui maps RGB to the nearest palette color on non-truecolor
  terminals; if the dark tints collapse to black there, the sign column and
  emphasis backgrounds still carry the signal. Do not use white/black literals
  for "normal" — always start from `GetAttributeForRole` so default terminal
  schemes work.

`DiffReviewDialog.Render()` change is minimal: instead of
`_diffPane.SetSource(strings)`, build
`_diffPane.Source = new DiffListDataSource(styledLines)` where
`styledLines = diff.Lines.Select(l => DiffLineStyler.Compose(l,
SyntaxTokenizer.Tokenize(l.Text, LanguageDetector.FromPath(file.Path)),
vm.ThreadsForDiffLine(l).Count > 0))`. Keep the existing same-file
`SelectedItem` preservation exactly as is (setting `Source` resets selection
the same way `SetSource` did). The file list ListView stays a plain
`ObservableCollection<string>`.

### 5. Rename-aware diffs

- `PrChangeEntryDto` gains `public string? SourceServerItem { get; init; }`
  and `public string? OriginalPath { get; init; }` (camelCase JSON matches the
  existing source-gen options; both are entry-level fields per the ADO
  contract).
- `FileChange` becomes `FileChange(string Path, FileChangeKind ChangeType,
  string? OriginalPath = null)` — optional param keeps all construction sites
  compiling.
- `GitApi.GetIterationChangesAsync` maps
  `OriginalPath: c.SourceServerItem ?? c.OriginalPath` (normalize to null when
  equal to `Path`).
- `PrDiffViewModel.ComputeCurrentDiffAsync`: fetch the base blob at
  `file.OriginalPath ?? file.Path` (source blob stays at `file.Path`). Add and
  Delete special cases unchanged. `ParseKind` already returns `Rename` for
  ADO's composite `"edit, rename"`. Threads keep anchoring at the new path
  (that is where ADO anchors them for renamed files); diff cache key stays
  `file.Path`.

## TDD boundary

Unit-tested (Core + pure Tui-project classes, tests FIRST): rename DTO
mapping, base-path selection, `IntraLineDiff`, the `DiffService` pairing pass,
`LanguageDetector`, `SyntaxTokenizer`, `DiffLineStyler` (incl. the
`DisplayText` format-compat assertion). PTY-verified only:
`DiffListDataSource.Render` + attribute mapping + dialog wiring (the only code
that touches `Move/SetAttribute/AddStr/GetAttributeForRole`).

## TDD task list (tree green after every task)

1. **Rename fields reach `FileChange`** —
   Failing tests first in `tests/Cobalt.Core.Tests/Ado/GitApiDiffTests.cs`:
   `GetIterationChanges_Maps_Rename_Source_Path` (changeEntry
   `{"changeType":"edit, rename","sourceServerItem":"/old/name.cs","item":{"path":"/new/name.cs"}}`
   → `ChangeType == Rename`, `OriginalPath == "/old/name.cs"`);
   `GetIterationChanges_Falls_Back_To_OriginalPath`; existing entries (no
   rename) → `OriginalPath` null.
   Implement: DTO fields in `PullRequestModels.cs`, `FileChange` optional
   param, `GitApi.cs` mapping.

2. **Rename-aware base fetch in the VM** —
   Failing tests first in `tests/Cobalt.Tui.Tests/ViewModels/PrDiffViewModelTests.cs`:
   `Renamed_File_Diffs_Base_At_Old_Path` (FakeDiffSource
   `Changes = [new FileChange("/new.cs", FileChangeKind.Rename, "/old.cs")]`,
   `Blobs[("/old.cs","base")] = "a\nb\n"`, `Blobs[("/new.cs","src")] = "a\nB\n"`
   → `Additions == 1 && Deletions == 1`, NOT 2 additions);
   `Rename_Without_Original_Path_Uses_New_Path` (regression).
   Implement: one line in `ComputeCurrentDiffAsync`.

3. **`IntraLineDiff` word-span model** —
   Failing tests first in `tests/Cobalt.Core.Tests/Text/IntraLineDiffTests.cs`:
   single word replaced → one span per side covering exactly that word
   (`"price"`→`"cost"`); insertion-only → old side empty; adjacent changed
   words merge into one span; identical lines → both empty; >60% changed →
   both empty (similarity guard); empty old line vs text → empty (guard);
   spans lie within bounds and are ordered/non-overlapping.
   Implement `src/Cobalt.Core/Text/IntraLineDiff.cs` (WordChunker + prefix-sum
   offsets + merge + guard).

4. **`DiffService` pairing pass fills `ChangedSpans`** —
   Failing tests first (extend `DiffServiceTests.cs`): a modified line
   (`"var total = price * q;"`→`"var total = cost * q;"`) yields one Removed
   and one Added line, each with a `ChangedSpans` entry over the differing
   word; a pure insertion has null/empty spans; a 2-removed/1-added block
   pairs only the first removed line; existing tests untouched and green
   (optional param default).
   Implement: `LineSpan`, `DiffLine.ChangedSpans`, post-processing scan in
   `DiffService.Unified`.

5. **`LanguageDetector`** —
   Failing tests first (`LanguageDetectorTests.cs`): `.cs`→CSharp;
   `.ts/.tsx/.js/.jsx/.mjs`→JsTs; `.json`→Json; `.py`→Python; `.txt`, no
   extension, trailing dot → None; case-insensitive (`.CS`).
   Implement `src/Cobalt.Core/Text/Syntax/Language.cs`.

6. **`SyntaxTokenizer`** —
   Failing tests first (`SyntaxTokenizerTests.cs`), per language:
   - Invariants: tokens partition `[0, line.Length)` exactly, ordered
     (property-style assertion helper reused by all cases).
   - C#: keywords (`public`, `var`, `return`), identifiers, string literal
     with `\"` escape, `// comment` swallows to EOL, numbers (`42`, `0x1F`,
     `3.14`), operators/punctuation.
   - JS/TS: `const`/`=>`/template `` ` `` string treated as a plain string to
     EOL-or-closing-backtick; `//` comment.
   - Python: `#` comment, `def`/`None`/`self` keywords, `'` strings.
   - JSON: `"key"` and `"value"` both String, `true/false/null` Keyword,
     numbers.
   - `Language.None` → single Plain token; empty line → empty list.
   Implement `src/Cobalt.Core/Text/Syntax/SyntaxTokenizer.cs`.

7. **`DiffLineStyler.Compose`** —
   Failing tests first (`DiffLineStylerTests.cs`):
   - `DisplayText` equals the current `FormatLine` output for
     marker/no-marker, added/removed/context (copy expected strings from
     `DiffReviewDialog.FormatLine` — `$"{marker}{oldNo,4} {newNo,4} {sign}{text}"`).
   - Runs partition `DisplayText` exactly; gutter runs flagged `IsGutter`,
     first code run starts at gutter length.
   - A token straddling a `ChangedSpans` boundary is split; inside-span runs
     have `Emphasis=true` and keep their `TokenKind`.
   - Context lines never have emphasis; all runs carry the line's
     `DiffLineKind`.
   Implement `src/Cobalt.Core/Text/DiffLineStyler.cs`.
   After this task the whole model is testable end-to-end without a terminal:
   old+new text → `DiffService.Unified` → tokens → `Compose` → styled runs.

8. **TUI adapter + dialog wiring** (no unit tests possible — PTY-verified;
   done when solution builds and all prior tests stay green) —
   Add `src/Cobalt.Tui/Screens/DiffListDataSource.cs` (data source +
   `Map(RunStyle, selected) → Attribute` per the design; `viewportX` slicing;
   width padding in line-kind bg; selected row = Focus).
   Modify `src/Cobalt.Tui/Screens/DiffReviewDialog.cs`: build styled lines in
   `Render()` (language from `vm.SelectedFile?.Path`), assign
   `_diffPane.Source`, delete `FormatLine`; add `case "j": / case "k":` in the
   dialog `KeyDown` forwarding to `_diffPane`/`_fileList` `MoveDown()/MoveUp()`
   on whichever has focus. Keep selection-preservation and thread-marker logic
   byte-for-byte.

9. **PTY smoke + ADR** — run the smoke below; write
   `docs/adr/0010-diff-pane-colored-listview-data-source.md`; update
   `docs/PLAN.md` if it tracks review polish.

## Files to add / modify

Add:
- `src/Cobalt.Core/Text/IntraLineDiff.cs`
- `src/Cobalt.Core/Text/Syntax/Language.cs` (enum + `LanguageDetector`)
- `src/Cobalt.Core/Text/Syntax/SyntaxTokenizer.cs` (+ `SyntaxToken`, `TokenKind`)
- `src/Cobalt.Core/Text/DiffLineStyler.cs` (+ `RunStyle`, `StyledRun`, `StyledLine`)
- `src/Cobalt.Tui/Screens/DiffListDataSource.cs`
- `tests/Cobalt.Core.Tests/Text/IntraLineDiffTests.cs`
- `tests/Cobalt.Core.Tests/Text/LanguageDetectorTests.cs`
- `tests/Cobalt.Core.Tests/Text/SyntaxTokenizerTests.cs`
- `tests/Cobalt.Core.Tests/Text/DiffLineStylerTests.cs`
- `docs/adr/0010-diff-pane-colored-listview-data-source.md`

Modify:
- `src/Cobalt.Core/Text/DiffService.cs` (`LineSpan`, `DiffLine.ChangedSpans`, pairing pass)
- `src/Cobalt.Core/Models/PullRequestModels.cs` (`PrChangeEntryDto` fields, `FileChange.OriginalPath`)
- `src/Cobalt.Core/Ado/GitApi.cs` (map source path)
- `src/Cobalt.Tui/ViewModels/PrDiffViewModel.cs` (base fetch at old path)
- `src/Cobalt.Tui/Screens/DiffReviewDialog.cs` (data source wiring, j/k)
- `tests/Cobalt.Core.Tests/Ado/GitApiDiffTests.cs`
- `tests/Cobalt.Core.Tests/Text/DiffServiceTests.cs`
- `tests/Cobalt.Tui.Tests/ViewModels/PrDiffViewModelTests.cs`

Untouched: `AdoHttp`, thread anchoring, file-list rendering, `KeymapRouter`.

## Verification

### Unit tests

`dotnet test` — full suite. New coverage: rename mapping (API JSON →
`FileChange.OriginalPath`), base-at-old-path diffing, word-span computation +
similarity guard, removed/added pairing, language detection, tokenizer
invariants per language, run composition/splitting/format-compat.

### PTY smoke (the only verification for task 8)

No real ADO needed: `IPrDiffSource` is public, so a probe app drives the real
dialog over an in-memory fake (same shape as `FakeDiffSource` in
`PrDiffViewModelTests.cs`).

1. Create `scratchpad/diffprobe2/` (csproj with a `ProjectReference` to
   `src/Cobalt.Tui/Cobalt.Tui.csproj`). `Program.cs`:
   `Application.Create().Init()`, build a fake source with:
   - `/calc.cs` Edit — base `"var total = price * quantity;\n"`, source
     `"var total = cost * quantity;\n"` (word-level change on a C# line);
   - `/renamed/new.cs` Rename with `OriginalPath = "/old.cs"` — base blob at
     `("/old.cs","base")` = 10 lines, source at `("/renamed/new.cs","src")` =
     same 10 lines with one edited;
   - one `PrThread` on `/calc.cs` line 1 (gutter `●`).
   Then `new DiffReviewDialog(app, new PrDiffViewModel(fake, pr),
   new EditorService(new ProcessEditorLauncher(_ => "true")), _ => {}).Show()`.
2. Drive with the existing harness (`scratchpad/ptyterm.py` answers the
   terminal queries; parse output with `pyte`, which tracks per-cell fg/bg —
   exactly how Option B was proven in `scratchpad/lvprobe/drive_lv.py`; reuse
   that script's cell-dump pattern). Assertions:
   - The `+` line of `/calc.cs`: cells have the added background; the cells
     spelling `cost` have a *different* (emphasis) background than the rest of
     that line; `var` cells' fg differs from `total` cells' fg (keyword vs
     identifier role).
   - The `-` line shows `price` with removed-emphasis background.
   - Gutter shows `●` on the thread line.
   - Press `]`: header reads `/renamed/new.cs   +1 -1` (NOT `+10 -0` — the
     rename fix, asserted from the clean-text screen).
   - Press `j` twice then `k`: pyte cursor/selected-row (Focus-colored row)
     moves accordingly; press `q`: app exits.
3. Manual spot check on a real ADO PR with a rename (best effort, not CI).

## ADR to write

`docs/adr/0010-diff-pane-colored-listview-data-source.md` — "Color the diff
pane through a custom `IListDataSource`, styled by a pure run model". Records:
(a) Terminal.Gui v2 has no rich-text ListView; the supported seam is
`IListDataSource.Render(ListView, selected, item, col, row, width, viewportX)`
calling `Move/SetAttribute/AddStr` on the ListView — PTY-proven on 2.4.16;
chosen over a custom View to keep ListView's scrolling/selection/keys, and
over `RowRender` which is whole-row only. (b) Foreground = syntax token role
(theme `VisualRole.Code*`), background = diff state (layering rule), decided
in a pure `DiffLineStyler` so everything except the attribute mapping is
unit-tested per ADR 0004. (c) Syntax highlighting is line-local (no cross-line
comment/string state) for C#, JS/TS, JSON, Python only — deliberate cut, fast
follow. (d) Renames diff base-at-`sourceServerItem` per ADR 0008's
client-side-diff model.
