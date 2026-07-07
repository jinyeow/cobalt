namespace Cobalt.Core.Config;

/// <summary>A user-fixable problem with the config file, phrased for display.</summary>
public sealed class ConfigException(string message) : Exception(message);
