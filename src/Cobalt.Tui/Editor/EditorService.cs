namespace Cobalt.Tui.Editor;

/// <summary>
/// The git-commit pattern: write text to a temp file, run $EDITOR on it, read it
/// back. Returns null when the user made no change or the editor exited non-zero.
/// </summary>
public sealed class EditorService(IEditorLauncher launcher)
{
    public async Task<string?> EditAsync(
        string initialContent, string extension, CancellationToken cancellationToken = default)
    {
        var path = Path.Join(
            Path.GetTempPath(), $"cobalt-{Guid.NewGuid():N}{extension}");
        try
        {
            // Inside the try so cancellation during the write still hits the cleanup.
            await File.WriteAllTextAsync(path, initialContent, cancellationToken).ConfigureAwait(false);
            var exitCode = await launcher.LaunchAsync(path, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                return null;
            }

            var edited = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return edited == initialContent ? null : edited;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // a leaked temp file is not worth surfacing
            }
        }
    }

    public static string ResolveEditorCommand(Func<string, string?> env) =>
        FirstNonEmpty(env("VISUAL")) ?? FirstNonEmpty(env("EDITOR")) ?? "vi";

    private static string? FirstNonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
