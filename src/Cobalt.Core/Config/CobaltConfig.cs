namespace Cobalt.Core.Config;

public sealed class CobaltConfig
{
    public CobaltConfig(string? defaultContext, IReadOnlyDictionary<string, AdoContext> contexts)
    {
        DefaultContext = defaultContext;
        Contexts = contexts;
    }

    public string? DefaultContext { get; }
    public IReadOnlyDictionary<string, AdoContext> Contexts { get; }

    /// <summary>Picks the active context: CLI override, then default_context, then a sole context.</summary>
    public AdoContext Resolve(string? nameOverride)
    {
        var name = nameOverride ?? DefaultContext;
        if (name is null)
        {
            if (Contexts.Count == 1)
            {
                return Contexts.Values.Single();
            }
            throw new ConfigException(
                $"multiple contexts defined but no default_context set; pick one of: {Names()}");
        }

        return Contexts.TryGetValue(name, out var ctx)
            ? ctx
            : throw new ConfigException($"context '{name}' not found; available: {Names()}");
    }

    private string Names() => string.Join(", ", Contexts.Keys.Order(StringComparer.Ordinal));
}
