using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lspeek.Tui.MetaModel;

/// <summary>
/// Generates JSON templates for LSP request/notification params based on the metamodel.
/// Templates include required properties with typed placeholders and optional properties marked.
/// </summary>
public class RequestTemplateGenerator
{
    private readonly LspMetaModelProvider _provider;
    private int _maxDepth = 5;

    public RequestTemplateGenerator(LspMetaModelProvider provider)
    {
        _provider = provider;
    }

    /// <summary>
    /// Generates a JSON template for the given LSP method's params.
    /// </summary>
    public string? GenerateTemplate(string method)
    {
        // Try as a request first, then notification
        var request = _provider.GetRequest(method);
        if (request?.Params is not null)
        {
            var json = GenerateForType(request.Params, 0);
            return FormatJson(json);
        }

        var notification = _provider.GetNotification(method);
        if (notification?.Params is not null)
        {
            var json = GenerateForType(notification.Params, 0);
            return FormatJson(json);
        }

        return null;
    }

    /// <summary>
    /// Returns the list of methods grouped by category (prefix before /).
    /// </summary>
    public static Dictionary<string, List<string>> GroupMethodsByCategory(IEnumerable<string> methods)
    {
        return methods
            .GroupBy(m =>
            {
                var slashIndex = m.IndexOf('/');
                return slashIndex >= 0 ? m[..slashIndex] : "other";
            })
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m).ToList());
    }

    private JsonNode? GenerateForType(LspType type, int depth)
    {
        if (depth > _maxDepth) return JsonValue.Create("<...>");

        return type.Kind switch
        {
            "reference" => GenerateForReference(type.Name!, depth),
            "base" => GenerateForBase(type.Name!),
            "array" => new JsonArray(GenerateForType(type.Element!, depth + 1)),
            "or" => GenerateForOr(type, depth),
            "and" => GenerateForAnd(type, depth),
            "literal" when type.Properties is not null => GenerateForStructureProperties(type.Properties, depth),
            "map" => new JsonObject(),
            "tuple" => new JsonArray(),
            "stringLiteral" => JsonValue.Create(type.Name ?? ""),
            "integerLiteral" => JsonValue.Create(0),
            "booleanLiteral" => JsonValue.Create(false),
            _ => JsonValue.Create($"<{type.Kind}>")
        };
    }

    private JsonNode? GenerateForReference(string name, int depth)
    {
        // Check if it's a structure
        var structure = _provider.GetStructure(name);
        if (structure is not null)
        {
            var allProps = _provider.GetAllProperties(name);
            return GenerateForStructureProperties(allProps, depth);
        }

        // Check if it's an enumeration
        var enumeration = _provider.GetEnumeration(name);
        if (enumeration is not null)
        {
            return GenerateForEnumeration(enumeration);
        }

        // Check if it's a type alias
        var alias = _provider.GetTypeAlias(name);
        if (alias is not null)
        {
            return GenerateForType(alias.Type, depth);
        }

        // Unknown reference — use placeholder
        return JsonValue.Create($"<{name}>");
    }

    private JsonObject GenerateForStructureProperties(IReadOnlyList<LspProperty> properties, int depth)
    {
        var obj = new JsonObject();

        foreach (var prop in properties)
        {
            // Only include required properties by default
            if (prop.Optional == true) continue;

            var value = GenerateForType(prop.Type, depth + 1);
            obj[prop.Name] = value;
        }

        return obj;
    }

    private static JsonNode? GenerateForBase(string name)
    {
        return name switch
        {
            "string" => JsonValue.Create("<string>"),
            "integer" or "uinteger" or "decimal" => JsonValue.Create(0),
            "boolean" => JsonValue.Create(false),
            "null" => null,
            "URI" or "DocumentUri" => JsonValue.Create("<uri>"),
            "RegExp" => JsonValue.Create("<regexp>"),
            _ => JsonValue.Create($"<{name}>")
        };
    }

    private JsonNode? GenerateForOr(LspType type, int depth)
    {
        // Pick the first non-null type
        if (type.Items is null || type.Items.Count == 0) return null;
        foreach (var item in type.Items)
        {
            if (item.Kind != "base" || item.Name != "null")
            {
                return GenerateForType(item, depth);
            }
        }
        return null;
    }

    private JsonNode? GenerateForAnd(LspType type, int depth)
    {
        // Merge all properties from the constituent types
        if (type.Items is null) return new JsonObject();

        var obj = new JsonObject();
        foreach (var item in type.Items)
        {
            var generated = GenerateForType(item, depth);
            if (generated is JsonObject innerObj)
            {
                foreach (var kvp in innerObj)
                {
                    obj[kvp.Key] = kvp.Value?.DeepClone();
                }
            }
        }
        return obj;
    }

    private static JsonNode? GenerateForEnumeration(LspEnumeration enumeration)
    {
        if (enumeration.Values.Count == 0)
            return JsonValue.Create(0);

        var first = enumeration.Values[0];
        if (first.Value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Number => JsonValue.Create(element.GetInt32()),
                JsonValueKind.String => JsonValue.Create(element.GetString()),
                _ => JsonValue.Create(first.Value?.ToString() ?? "")
            };
        }

        return JsonValue.Create(first.Value?.ToString() ?? "");
    }

    private static string FormatJson(JsonNode? node)
    {
        if (node is null) return "null";
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
