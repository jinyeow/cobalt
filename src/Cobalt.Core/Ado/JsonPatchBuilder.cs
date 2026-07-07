using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cobalt.Core.Ado;

/// <summary>
/// Builds an Azure DevOps JSON Patch document (`application/json-patch+json`).
/// ADO uses `add` to set-or-replace a field and `remove` to clear it. Built with
/// JsonNode so heterogeneous values (string/number) serialize without a
/// source-gen context (ADR 0002).
/// </summary>
public sealed class JsonPatchBuilder
{
    private readonly JsonArray _ops = [];

    public bool HasOperations => _ops.Count > 0;

    public JsonPatchBuilder SetField(string field, string value) => Add(field, JsonValue.Create(value));

    public JsonPatchBuilder SetField(string field, double value) => Add(field, JsonValue.Create(value));

    public JsonPatchBuilder SetField(string field, int value) => Add(field, JsonValue.Create(value));

    public JsonPatchBuilder RemoveField(string field)
    {
        _ops.Add(new JsonObject
        {
            ["op"] = "remove",
            ["path"] = $"/fields/{field}",
        });
        return this;
    }

    private JsonPatchBuilder Add(string field, JsonNode? value)
    {
        _ops.Add(new JsonObject
        {
            ["op"] = "add",
            ["path"] = $"/fields/{field}",
            ["value"] = value,
        });
        return this;
    }

    // Relaxed escaping keeps HTML (<, >, &) in field values readable; the body is
    // JSON, not HTML, so this is safe and matches what ADO round-trips.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => _ops.ToJsonString(SerializerOptions);
}
