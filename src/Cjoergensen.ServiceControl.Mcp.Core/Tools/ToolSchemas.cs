using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cjoergensen.ServiceControl.Mcp.Tools;

/// <summary>
/// The MCP SDK describes an optional parameter such as <c>string? status = null</c> as <c>{"type":["string","null"],"default":null}</c>. That is valid
/// JSON Schema, but some clients and model back-ends accept only a single type per property, and silently drop a tool whose schema they cannot read.
/// Every parameter here is optional simply by being omitted, so the plain type says the same thing to every client.
/// </summary>
internal static class ToolSchemas
{
    public static JsonElement WithoutNullableTypes(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText());
        Simplify(node);
        return JsonSerializer.SerializeToElement(node);
    }

    static void Simplify(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["type"] is JsonArray types)
                {
                    var kept = types.Select(t => t?.GetValue<string>()).Where(t => t is not null and not "null").ToList();
                    if (kept.Count == 1)
                    {
                        obj["type"] = kept[0];
                    }
                }

                if (obj.TryGetPropertyValue("default", out var value) && value is null)
                {
                    obj.Remove("default");
                }

                foreach (var child in obj.ToList())
                {
                    Simplify(child.Value);
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    Simplify(item);
                }

                break;
        }
    }
}
