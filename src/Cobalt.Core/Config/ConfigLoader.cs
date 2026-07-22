using Tomlyn;
using Tomlyn.Model;

namespace Cobalt.Core.Config;

public static class ConfigLoader
{
    public static CobaltConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException(
                $"""
                no config file at {path}

                create one like:

                  default_context = "work"

                  [contexts.work]
                  organization = "https://dev.azure.com/YOUR_ORG"
                  project = "YOUR_PROJECT"
                """);
        }
        return Parse(File.ReadAllText(path));
    }

    public static CobaltConfig Parse(string toml)
    {
        TomlTable? root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(toml);
        }
        catch (TomlException ex)
        {
            throw new ConfigException($"config is not valid TOML: {ex.Message}");
        }
        if (root is null)
        {
            throw new ConfigException("config file is empty");
        }

        var defaultContext = root.TryGetValue("default_context", out var dc) ? dc as string : null;

        var contexts = new Dictionary<string, AdoContext>(StringComparer.Ordinal);
        if (root.TryGetValue("contexts", out var rawContexts) && rawContexts is TomlTable contextTable)
        {
            foreach (var (name, value) in contextTable)
            {
                if (value is not TomlTable t)
                {
                    throw new ConfigException($"[contexts.{name}] must be a table");
                }
                contexts[name] = ParseContext(name, t);
            }
        }

        if (contexts.Count == 0)
        {
            throw new ConfigException("config defines no [contexts.*] sections");
        }
        if (defaultContext is not null && !contexts.ContainsKey(defaultContext))
        {
            throw new ConfigException(
                $"default_context '{defaultContext}' has no matching [contexts.{defaultContext}] section");
        }

        return new CobaltConfig(defaultContext, contexts, ParseTheme(root), ParseKeys(root));
    }

    private static KeysConfig ParseKeys(TomlTable root)
    {
        if (!root.TryGetValue("keys", out var raw))
        {
            return KeysConfig.Empty;
        }
        if (raw is not TomlTable keysTable)
        {
            throw new ConfigException("[keys] must be a table of [keys.<scope>] sections, e.g. [keys.global]");
        }

        var scopes = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
        // TOML keys are case-sensitive, so `[keys.global]` and `[keys.Global]` are two
        // distinct table entries here; both resolve to the same KeyScope downstream
        // (case-insensitively), so silently letting the second overwrite the first would
        // drop one's bindings without a trace. Track normalized names to catch that.
        var seenScopeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (scopeName, scopeValue) in keysTable)
        {
            if (seenScopeNames.TryGetValue(scopeName, out var firstSpelling))
            {
                throw new ConfigException(
                    $"[keys.{scopeName}] duplicates [keys.{firstSpelling}]; scope names are case-insensitive");
            }
            seenScopeNames[scopeName] = scopeName;

            if (scopeValue is not TomlTable scopeTable)
            {
                throw new ConfigException(
                    $"[keys.{scopeName}] must be a table of command-name = \"tokens\" entries");
            }

            var commands = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var (commandName, commandValue) in scopeTable)
            {
                commands[commandName] = ParseKeySequences(scopeName, commandName, commandValue);
            }
            scopes[scopeName] = commands;
        }
        return new KeysConfig(scopes);
    }

    private static IReadOnlyList<string> ParseKeySequences(string scopeName, string commandName, object? value)
    {
        switch (value)
        {
            case string sequence:
                if (sequence.Length == 0)
                {
                    return []; // a bare "" is the documented unbind syntax
                }
                if (string.IsNullOrWhiteSpace(sequence))
                {
                    throw new ConfigException(
                        $"[keys.{scopeName}] {commandName} is whitespace-only; use \"\" to unbind");
                }
                return [sequence];
            case TomlArray array:
                var sequences = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in array)
                {
                    if (item is not string s)
                    {
                        throw new ConfigException(
                            $"[keys.{scopeName}] {commandName} must be a string or array of strings");
                    }
                    if (string.IsNullOrWhiteSpace(s))
                    {
                        throw new ConfigException(
                            $"[keys.{scopeName}] {commandName} has an empty or whitespace-only entry in its array " +
                            "(a bare \"\" outside an array unbinds instead)");
                    }
                    if (!seen.Add(s))
                    {
                        throw new ConfigException(
                            $"[keys.{scopeName}] {commandName} binds '{s}' twice");
                    }
                    sequences.Add(s);
                }
                return sequences;
            default:
                throw new ConfigException(
                    $"[keys.{scopeName}] {commandName} must be a string or array of strings");
        }
    }

    private static ThemeChoice ParseTheme(TomlTable table)
    {
        // Absent => Dark (the product default), so an old/empty config is unchanged.
        if (!table.TryGetValue("theme", out var raw))
        {
            return ThemeChoice.Dark;
        }
        if (raw is string text && ThemeChoices.TryParse(text.Trim(), out var choice))
        {
            return choice;
        }
        throw new ConfigException(
            $"theme must be one of {string.Join(", ", ThemeChoices.Names.Select(n => $"\"{n}\""))}, got '{raw}'");
    }

    private static AdoContext ParseContext(string name, TomlTable table)
    {
        // `theme` is a root-level setting. In TOML a key written after a `[contexts.*]` header
        // binds to that table, so a `theme = ...` line appended to the end of a config (the
        // natural place to add it) lands here and would otherwise be silently ignored — leaving
        // the app on the default theme with no hint why. Fail loudly and point to the fix.
        if (table.ContainsKey("theme"))
        {
            throw new ConfigException(
                $"[contexts.{name}] has a 'theme' key, but theme is a top-level setting; "
                + "move it above the [contexts.*] sections");
        }

        var organization = table.TryGetValue("organization", out var o) ? o as string : null;
        var project = table.TryGetValue("project", out var p) ? p as string : null;

        if (string.IsNullOrWhiteSpace(organization))
        {
            throw new ConfigException($"[contexts.{name}] is missing 'organization'");
        }
        if (string.IsNullOrWhiteSpace(project))
        {
            throw new ConfigException($"[contexts.{name}] is missing 'project'");
        }

        return new AdoContext
        {
            Name = name,
            OrganizationUrl = NormalizeOrganization(name, organization),
            Project = project,
            PrScope = ParsePrScope(name, table),
        };
    }

    private static PrScope ParsePrScope(string contextName, TomlTable table)
    {
        // Absent => Org (the product default). Org scope needs only the org URL;
        // Project scope reuses the required 'project' key, so both are always valid.
        if (!table.TryGetValue("pr_scope", out var raw))
        {
            return PrScope.Org;
        }
        return (raw as string) switch
        {
            "org" => PrScope.Org,
            "project" => PrScope.Project,
            _ => throw new ConfigException(
                $"[contexts.{contextName}] pr_scope must be \"org\" or \"project\", got '{raw}'"),
        };
    }

    private static Uri NormalizeOrganization(string contextName, string organization)
    {
        // A bare name like "contoso" means https://dev.azure.com/contoso.
        var isUrl = organization.Contains("://", StringComparison.Ordinal);
        if (!isUrl && organization.Contains('/', StringComparison.Ordinal))
        {
            throw new ConfigException(
                $"[contexts.{contextName}] organization '{organization}' looks like a partial URL; " +
                "use either a bare org name or a full https:// URL");
        }
        var url = isUrl ? organization : $"https://dev.azure.com/{organization}";

        if (!Uri.TryCreate(url.TrimEnd('/'), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ConfigException(
                $"[contexts.{contextName}] organization must be an https URL or a bare org name, got '{organization}'");
        }
        return uri;
    }
}
