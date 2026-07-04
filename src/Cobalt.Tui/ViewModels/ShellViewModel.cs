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
public sealed class ShellViewModel(IReadOnlyList<string> contextNames, string initialContext)
{
    public MessageLog Messages { get; } = new();

    public AppSection ActiveSection { get; private set; } = AppSection.WorkItems;
    public string ContextName { get; private set; } = initialContext;
    public string? UserName { get; private set; }
    public IReadOnlyList<string> ContextNames => contextNames;

    public event Action? SectionChanged;
    public event Action? QuitRequested;
    public event Action? HelpRequested;
    public event Action? MessagesRequested;
    public event Action? PickContextRequested;

    /// <summary>Raised with a validated context name; the shell performs the actual reconnect.</summary>
    public event Action<string>? ContextSwitchRequested;

    public string StatusLine =>
        $" ctx:{ContextName}" + (UserName is null ? "" : $"  {UserName}");

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
            case PaletteActionKind.Unknown:
                Messages.Error(action.Argument);
                break;
            case PaletteActionKind.None:
            default:
                break;
        }
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
