namespace Cobalt.Core.Config;

/// <summary>
/// Parsed <c>[keys.&lt;scope&gt;]</c> overrides from config.toml: scope name (a lowercase
/// <c>KeyScope</c> value, e.g. "global") -&gt; command name (kebab-case, e.g. "move-down")
/// -&gt; token sequences to bind (an empty list unbinds the command in that scope). Core
/// stays UI-free: mapping scope/command names onto Cobalt.Tui's <c>KeyScope</c>/
/// <c>AppCommand</c> happens in <c>KeyBindingTable.FromConfig</c>.
/// </summary>
public sealed class KeysConfig(IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> scopes)
{
    public static KeysConfig Empty { get; } =
        new(new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>());

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Scopes { get; } = scopes;
}
