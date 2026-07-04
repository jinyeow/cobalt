namespace Cobalt.Tui.Editor;

/// <summary>Runs the user's editor on a file and reports its exit code (TUI suspend lives in the implementation).</summary>
public interface IEditorLauncher
{
    Task<int> LaunchAsync(string filePath, CancellationToken cancellationToken = default);
}
