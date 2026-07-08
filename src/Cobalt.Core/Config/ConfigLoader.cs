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

        return new CobaltConfig(defaultContext, contexts, ParseTheme(root));
    }

    private static ThemeChoice ParseTheme(TomlTable table)
    {
        // Absent => Dark (the product default), so an old/empty config is unchanged.
        if (!table.TryGetValue("theme", out var raw))
        {
            return ThemeChoice.Dark;
        }
        return (raw as string)?.ToLowerInvariant() switch
        {
            "dark" => ThemeChoice.Dark,
            "light" => ThemeChoice.Light,
            "system" => ThemeChoice.System,
            _ => throw new ConfigException(
                $"theme must be \"dark\", \"light\", or \"system\", got '{raw}'"),
        };
    }

    private static AdoContext ParseContext(string name, TomlTable table)
    {
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
