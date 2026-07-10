using Cobalt.Core.Config;
using Cobalt.Tui.Input;

namespace Cobalt.Tui.ViewModels;

public enum AppSection
{
    WorkItems,
    PullRequests,
}

/// <summary>
/// Top-level app state: active section, active context, status line, and palette
/// dispatch. No Terminal.Gui types (ADR 0004) — the shell view binds to this.
/// </summary>
public sealed class ShellViewModel(
    IReadOnlyList<string> contextNames,
    string initialContext,
    PrScope initialScope = PrScope.Org,
    ThemeChoice initialTheme = ThemeChoice.Dark)
{
    public MessageLog Messages { get; } = new();

    public AppSection ActiveSection { get; private set; } = AppSection.WorkItems;
    public string ContextName { get; private set; } = initialContext;
    public string? UserName { get; private set; }
    public IReadOnlyList<string> ContextNames => contextNames;

    /// <summary>The active PR-list breadth, shown in the status line and flipped by <c>:scope</c>.</summary>
    public PrScope Scope { get; private set; } = initialScope;

    /// <summary>Whether the work-item list shows completed states; default hides them (<c>:done</c>).</summary>
    public bool IncludeCompletedWorkItems { get; private set; }

    /// <summary>Active single-project narrowing for both lists, or null for all projects (<c>:project</c>).</summary>
    public string? ProjectFilter { get; private set; }

    /// <summary>The active colour theme, shown in messages and flipped by <c>:theme</c>.</summary>
    public ThemeChoice CurrentTheme { get; private set; } = initialTheme;

    public event Action? SectionChanged;
    public event Action? QuitRequested;
    public event Action? HelpRequested;
    public event Action? MessagesRequested;
    public event Action? PickContextRequested;

    /// <summary>Raised with a validated context name; the shell performs the actual reconnect.</summary>
    public event Action<string>? ContextSwitchRequested;

    /// <summary>Raised with the new PR scope; the shell repoints the adapters and reloads the lists.</summary>
    public event Action<PrScope>? ScopeChangeRequested;

    /// <summary>Raised with whether completed items are now included; the shell reloads the work-item list.</summary>
    public event Action<bool>? DoneFilterChanged;

    /// <summary>Raised with the new project narrowing (null clears it); the shell reloads both lists.</summary>
    public event Action<string?>? ProjectFilterChanged;

    /// <summary>Raised with the new theme; the shell re-resolves the preset and repaints.</summary>
    public event Action<ThemeChoice>? ThemeChangeRequested;

    public string StatusLine =>
        $" context:{ContextName}  scope:{ScopeName(Scope)}" + (UserName is null ? "" : $"  {UserName}");

    private static string ScopeName(PrScope scope) => scope == PrScope.Org ? "org" : "project";

    /// <summary>Handles shell-level commands; returns false if the active screen should get it instead.</summary>
    public bool HandleCommand(AppCommand command)
    {
        switch (command)
        {
            case AppCommand.SectionWorkItems:
                SetSection(AppSection.WorkItems);
                return true;
            case AppCommand.SectionPullRequests:
                SetSection(AppSection.PullRequests);
                return true;
            case AppCommand.NextTab:
            case AppCommand.PrevTab:
            case AppCommand.NextSection:
            case AppCommand.PrevSection:
                // Two sections today, so next/prev both toggle; wrapping is implicit.
                SetSection(ActiveSection == AppSection.WorkItems
                    ? AppSection.PullRequests
                    : AppSection.WorkItems);
                return true;
            case AppCommand.Help:
                HelpRequested?.Invoke();
                return true;
            case AppCommand.Quit:
                QuitRequested?.Invoke();
                return true;
            case AppCommand.Back:
                // From the top-level lists there's nothing to close, so `q` exits the app
                // (matches the user's expectation; `:q` still works too).
                QuitRequested?.Invoke();
                return true;
            default:
                return false;
        }
    }

    public void HandlePaletteInput(string input)
    {
        var action = PaletteCommandParser.Parse(input);
        switch (action.Kind)
        {
            case PaletteActionKind.Quit:
                QuitRequested?.Invoke();
                break;
            case PaletteActionKind.Help:
                HelpRequested?.Invoke();
                break;
            case PaletteActionKind.Messages:
                MessagesRequested?.Invoke();
                break;
            case PaletteActionKind.PickContext:
                PickContextRequested?.Invoke();
                break;
            case PaletteActionKind.SwitchContext when contextNames.Contains(action.Argument, StringComparer.Ordinal):
                ContextSwitchRequested?.Invoke(action.Argument);
                break;
            case PaletteActionKind.SwitchContext:
                Messages.Error(
                    $"unknown context '{action.Argument}' (available: {string.Join(", ", contextNames)})");
                break;
            case PaletteActionKind.SetScope:
                SetScope(action.Argument);
                break;
            case PaletteActionKind.ToggleDone:
                SetDoneFilter(action.Argument);
                break;
            case PaletteActionKind.SetProjectFilter:
                SetProjectFilter(action.Argument);
                break;
            case PaletteActionKind.SetTheme:
                SetTheme(action.Argument);
                break;
            case PaletteActionKind.Unknown:
                Messages.Error(action.Argument);
                break;
            case PaletteActionKind.None:
            default:
                break;
        }
    }

    private void SetScope(string argument)
    {
        // Bare `:scope` just reports the current value.
        if (argument.Length == 0)
        {
            Messages.Info($"pr scope: {ScopeName(Scope)} (switch with :scope org|project)");
            return;
        }
        switch (argument.ToLowerInvariant())
        {
            case "org":
            case "project":
                var next = argument.Equals("org", StringComparison.OrdinalIgnoreCase) ? PrScope.Org : PrScope.Project;
                if (next == Scope)
                {
                    Messages.Info($"pr scope already {ScopeName(Scope)}");
                    return;
                }
                Scope = next;
                Messages.Info($"pr scope: {ScopeName(Scope)}");
                ScopeChangeRequested?.Invoke(Scope);
                break;
            default:
                Messages.Error($"unknown scope '{argument}' (use: org or project)");
                break;
        }
    }

    private void SetTheme(string argument)
    {
        // Bare `:theme` just reports the current value.
        if (argument.Length == 0)
        {
            Messages.Info($"theme: {ThemeName(CurrentTheme)} (switch with :theme dark|light|system)");
            return;
        }
        switch (argument.ToLowerInvariant())
        {
            case "dark":
            case "light":
            case "system":
                var next = Enum.Parse<ThemeChoice>(argument, ignoreCase: true);
                // `system` re-resolves against the live OS setting, so re-issuing it is a
                // meaningful refresh (e.g. to recover if the OS-follow watcher stopped);
                // re-issuing a fixed theme is a genuine no-op.
                if (next == CurrentTheme && next != ThemeChoice.System)
                {
                    Messages.Info($"theme already {ThemeName(CurrentTheme)}");
                    return;
                }
                CurrentTheme = next;
                Messages.Info(next == ThemeChoice.System
                    ? "theme: system (re-synced to OS)"
                    : $"theme: {ThemeName(CurrentTheme)}");
                ThemeChangeRequested?.Invoke(CurrentTheme);
                break;
            default:
                Messages.Error($"unknown theme '{argument}' (use: dark, light, or system)");
                break;
        }
    }

    private static string ThemeName(ThemeChoice theme) => theme.ToString().ToLowerInvariant();

    private static string DoneStateName(bool includeCompleted) => includeCompleted ? "showing done" : "hiding done";

    private void SetDoneFilter(string argument)
    {
        // Bare `:done` just reports current state; show|hide are the explicit toggles.
        if (argument.Length == 0)
        {
            Messages.Info($"work items: {DoneStateName(IncludeCompletedWorkItems)} (toggle with :done show|hide)");
            return;
        }
        switch (argument.ToLowerInvariant())
        {
            case "show":
            case "hide":
                var include = argument.Equals("show", StringComparison.OrdinalIgnoreCase);
                if (include == IncludeCompletedWorkItems)
                {
                    Messages.Info($"work items already {DoneStateName(IncludeCompletedWorkItems)}");
                    return;
                }
                IncludeCompletedWorkItems = include;
                Messages.Info($"work items: {DoneStateName(IncludeCompletedWorkItems)}");
                DoneFilterChanged?.Invoke(IncludeCompletedWorkItems);
                break;
            default:
                Messages.Error($"unknown :done option '{argument}' (use: show or hide)");
                break;
        }
    }

    private void SetProjectFilter(string argument)
    {
        // Bare `:project` clears an active filter, or reports that none is set.
        if (argument.Length == 0)
        {
            if (ProjectFilter is null)
            {
                Messages.Info("no project filter (set with :project NAME)");
                return;
            }
            ProjectFilter = null;
            Messages.Info("project filter cleared");
            ProjectFilterChanged?.Invoke(null);
            return;
        }
        if (string.Equals(ProjectFilter, argument, StringComparison.Ordinal))
        {
            Messages.Info($"project filter already '{argument}'");
            return;
        }
        ProjectFilter = argument;
        Messages.Info($"project filter: {argument}");
        ProjectFilterChanged?.Invoke(argument);
    }

    public void OnContextSwitched(string contextName, string? userName)
    {
        ContextName = contextName;
        UserName = userName;
        Messages.Info($"switched to context '{contextName}'");
    }

    public void OnUserResolved(string userName)
    {
        UserName = userName;
        Messages.Info($"signed in as {userName}");
    }

    private void SetSection(AppSection section)
    {
        if (ActiveSection == section)
        {
            return;
        }
        ActiveSection = section;
        SectionChanged?.Invoke();
    }
}
