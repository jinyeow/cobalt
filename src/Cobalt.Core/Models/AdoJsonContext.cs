using System.Text.Json.Serialization;

namespace Cobalt.Core.Models;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConnectionData))]
public sealed partial class AdoJsonContext : JsonSerializerContext;
