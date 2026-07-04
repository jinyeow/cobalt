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
        // $EDITOR may carry arguments ("code --wait"); first token is the executable.
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

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
}
