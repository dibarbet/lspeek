using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lspeek.Tui.MetaModel;

/// <summary>
/// Deserialized LSP 3.18 metamodel types.
/// These mirror the schema at https://github.com/microsoft/language-server-protocol/tree/gh-pages/_specifications/lsp/3.18/metaModel
/// </summary>
public class MetaModelRoot
{
    [JsonPropertyName("metaData")]
    public MetaData MetaData { get; set; } = new();

    [JsonPropertyName("requests")]
    public List<LspRequest> Requests { get; set; } = [];

    [JsonPropertyName("notifications")]
    public List<LspNotification> Notifications { get; set; } = [];

    [JsonPropertyName("structures")]
    public List<LspStructure> Structures { get; set; } = [];

    [JsonPropertyName("enumerations")]
    public List<LspEnumeration> Enumerations { get; set; } = [];

    [JsonPropertyName("typeAliases")]
    public List<LspTypeAlias> TypeAliases { get; set; } = [];
}

public class MetaData
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}

public class LspRequest
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("typeName")]
    public string? TypeName { get; set; }

    [JsonPropertyName("messageDirection")]
    public string MessageDirection { get; set; } = "";

    [JsonPropertyName("params")]
    public LspType? Params { get; set; }

    [JsonPropertyName("result")]
    public LspType? Result { get; set; }

    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }

    [JsonPropertyName("since")]
    public string? Since { get; set; }

    [JsonPropertyName("proposed")]
    public bool? Proposed { get; set; }

    [JsonPropertyName("deprecated")]
    public string? Deprecated { get; set; }
}

public class LspNotification
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("typeName")]
    public string? TypeName { get; set; }

    [JsonPropertyName("messageDirection")]
    public string MessageDirection { get; set; } = "";

    [JsonPropertyName("params")]
    public LspType? Params { get; set; }

    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }

    [JsonPropertyName("since")]
    public string? Since { get; set; }

    [JsonPropertyName("proposed")]
    public bool? Proposed { get; set; }

    [JsonPropertyName("deprecated")]
    public string? Deprecated { get; set; }
}

public class LspStructure
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("properties")]
    public List<LspProperty> Properties { get; set; } = [];

    [JsonPropertyName("extends")]
    public List<LspType>? Extends { get; set; }

    [JsonPropertyName("mixins")]
    public List<LspType>? Mixins { get; set; }

    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }

    [JsonPropertyName("since")]
    public string? Since { get; set; }

    [JsonPropertyName("proposed")]
    public bool? Proposed { get; set; }

    [JsonPropertyName("deprecated")]
    public string? Deprecated { get; set; }
}

public class LspProperty
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public LspType Type { get; set; } = new();

    [JsonPropertyName("optional")]
    public bool? Optional { get; set; }

    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }

    [JsonPropertyName("since")]
    public string? Since { get; set; }

    [JsonPropertyName("proposed")]
    public bool? Proposed { get; set; }

    [JsonPropertyName("deprecated")]
    public string? Deprecated { get; set; }
}

public class LspEnumeration
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public LspType Type { get; set; } = new();

    [JsonPropertyName("values")]
    public List<LspEnumerationEntry> Values { get; set; } = [];

    [JsonPropertyName("supportsCustomValues")]
    public bool? SupportsCustomValues { get; set; }

    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }
}

public class LspEnumerationEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("value")]
    public object? Value { get; set; }

    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }
}

public class LspTypeAlias
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public LspType Type { get; set; } = new();

    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }
}

/// <summary>
/// Discriminated union for LSP type references.
/// The "kind" property determines which fields are relevant.
/// </summary>
public class LspType
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    // For kind = "reference", "base"
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    // For kind = "array"
    [JsonPropertyName("element")]
    public LspType? Element { get; set; }

    // For kind = "or", "and", "tuple"
    [JsonPropertyName("items")]
    public List<LspType>? Items { get; set; }

    // For kind = "map": key is a MapKeyType, value is a Type
    [JsonPropertyName("key")]
    public LspType? Key { get; set; }

    // Polymorphic "value" field — interpretation depends on kind:
    //   map → LspType, literal → StructureLiteral, stringLiteral → string,
    //   integerLiteral → number, booleanLiteral → bool
    [JsonPropertyName("value")]
    public JsonElement? RawValue { get; set; }

    // For kind = "literal" (inline structure)
    [JsonPropertyName("properties")]
    public List<LspProperty>? Properties { get; set; }

    /// <summary>
    /// For kind = "map", returns the value type.
    /// </summary>
    [JsonIgnore]
    public LspType? MapValue =>
        Kind == "map" && RawValue.HasValue
            ? JsonSerializer.Deserialize<LspType>(RawValue.Value.GetRawText())
            : null;
}

public class LspStructureLiteral
{
    [JsonPropertyName("properties")]
    public List<LspProperty> Properties { get; set; } = [];

    [JsonPropertyName("deprecated")]
    public string? Deprecated { get; set; }

    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }
}
