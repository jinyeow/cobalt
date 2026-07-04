using System.Diagnostics;

namespace Cobalt.Tui.Editor;

/// <summary>
/// Runs $VISUAL/$EDITOR attached to the real terminal. The TUI must be paused
/// around this call; the shell passes suspend/resume hooks for that.
/// </summary>
public sealed class ProcessEditorLauncher(
    Func<string, string?> env,
    Action? beforeLaunch = null,
    Action? afterExit = null) : IEditorLauncher
{
    public async Task<int> LaunchAsync(string filePath, CancellationToken cancellationToken = default)
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

        beforeLaunch?.Invoke();
        try
        {
            using var process = Process.Start(info)
                ?? throw new InvalidOperationException($"could not start editor '{parts[0]}'");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            afterExit?.Invoke();
        }
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
