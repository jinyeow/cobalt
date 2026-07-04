using System.ComponentModel;
using System.Diagnostics;

namespace Cobalt.Tui.Editor;

/// <summary>
/// Runs $VISUAL/$EDITOR attached to the real terminal. Because a full-screen
/// editor fights Terminal.Gui for the tty, the launch runs inside an
/// <see cref="ITerminalSuspender"/>: the suspender parks the UI thread, hands the
/// terminal to the child, and restores the TUI on exit. The default
/// <see cref="InlineTerminalSuspender"/> keeps headless/test construction working;
/// the shell wires the Terminal.Gui suspender for the production path.
/// </summary>
public sealed class ProcessEditorLauncher(
    Func<string, string?> env,
    ITerminalSuspender? suspender = null) : IEditorLauncher
{
    private readonly ITerminalSuspender _suspender = suspender ?? new InlineTerminalSuspender();

    public Task<int> LaunchAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var command = EditorService.ResolveEditorCommand(env);
        // $EDITOR may carry arguments ("code --wait") and quoted paths ("/opt/My Editor/nvim").
        var parts = TokenizeCommand(command);

        var info = new ProcessStartInfo
        {
            FileName = parts[0],
            UseShellExecute = false,
        };
        foreach (var arg in parts.Skip(1))
        {
            info.ArgumentList.Add(arg);
        }
        info.ArgumentList.Add(filePath);

        // The body runs on the parked UI thread, so WaitForExit is synchronous by
        // design: nothing may draw while the child owns the terminal.
        return _suspender.RunSuspendedAsync(() =>
        {
            Process process;
            try
            {
                process = Process.Start(info)
                    ?? throw new EditorLaunchException($"could not start editor '{parts[0]}'");
            }
            catch (Win32Exception ex)
            {
                throw new EditorLaunchException(
                    $"could not start editor '{parts[0]}' — check $VISUAL/$EDITOR", ex);
            }
            using (process)
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }, cancellationToken);
    }

    /// <summary>Splits a command on spaces, honoring single/double quotes around paths or args.</summary>
    internal static List<string> TokenizeCommand(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';

        foreach (var c in command)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }
        return tokens.Count > 0 ? tokens : ["vi"];
    }
}
