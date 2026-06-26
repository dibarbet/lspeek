using System.Reflection;
using System.Text.Json;

namespace Lspeek.Tui.MetaModel;

/// <summary>
/// Loads and queries the LSP 3.18 metamodel.
/// </summary>
public class LspMetaModelProvider
{
    private readonly MetaModelRoot _model;
    private readonly Dictionary<string, LspStructure> _structures;
    private readonly Dictionary<string, LspEnumeration> _enumerations;
    private readonly Dictionary<string, LspTypeAlias> _typeAliases;

    private LspMetaModelProvider(MetaModelRoot model)
    {
        _model = model;
        _structures = model.Structures.ToDictionary(s => s.Name, StringComparer.Ordinal);
        _enumerations = model.Enumerations.ToDictionary(e => e.Name, StringComparer.Ordinal);
        _typeAliases = model.TypeAliases.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public static LspMetaModelProvider Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("metaModel.json"))
            ?? throw new InvalidOperationException("LSP metamodel resource not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        var model = JsonSerializer.Deserialize<MetaModelRoot>(stream)
            ?? throw new InvalidOperationException("Failed to deserialize LSP metamodel.");

        return new LspMetaModelProvider(model);
    }

    /// <summary>
    /// Gets all requests that a client can send to the server.
    /// </summary>
    public IReadOnlyList<LspRequest> GetClientRequests()
    {
        return _model.Requests
            .Where(r => r.MessageDirection is "clientToServer" or "both")
            .Where(r => r.Proposed != true)
            .OrderBy(r => r.Method)
            .ToList();
    }

    /// <summary>
    /// Gets all notifications that a client can send to the server.
    /// </summary>
    public IReadOnlyList<LspNotification> GetClientNotifications()
    {
        return _model.Notifications
            .Where(n => n.MessageDirection is "clientToServer" or "both")
            .Where(n => n.Proposed != true)
            .OrderBy(n => n.Method)
            .ToList();
    }

    public LspRequest? GetRequest(string method)
    {
        return _model.Requests.FirstOrDefault(r => r.Method == method);
    }

    public LspNotification? GetNotification(string method)
    {
        return _model.Notifications.FirstOrDefault(n => n.Method == method);
    }

    public LspStructure? GetStructure(string name)
    {
        return _structures.GetValueOrDefault(name);
    }

    /// <summary>
    /// Gets all properties for a structure, including inherited ones from extends and mixins.
    /// </summary>
    public List<LspProperty> GetAllProperties(string structureName)
    {
        var visited = new HashSet<string>();
        var properties = new List<LspProperty>();
        CollectProperties(structureName, properties, visited);
        return properties;
    }

    public LspEnumeration? GetEnumeration(string name) => _enumerations.GetValueOrDefault(name);
    public LspTypeAlias? GetTypeAlias(string name) => _typeAliases.GetValueOrDefault(name);

    private void CollectProperties(string structureName, List<LspProperty> properties, HashSet<string> visited)
    {
        if (!visited.Add(structureName))
            return;

        if (!_structures.TryGetValue(structureName, out var structure))
            return;

        // Collect properties from base types (extends)
        if (structure.Extends is not null)
        {
            foreach (var baseType in structure.Extends)
            {
                if (baseType.Kind == "reference" && baseType.Name is not null)
                {
                    CollectProperties(baseType.Name, properties, visited);
                }
            }
        }

        // Collect properties from mixins
        if (structure.Mixins is not null)
        {
            foreach (var mixin in structure.Mixins)
            {
                if (mixin.Kind == "reference" && mixin.Name is not null)
                {
                    CollectProperties(mixin.Name, properties, visited);
                }
            }
        }

        // Add own properties (avoiding duplicates by name)
        var existingNames = properties.Select(p => p.Name).ToHashSet();
        foreach (var prop in structure.Properties)
        {
            if (existingNames.Add(prop.Name))
            {
                properties.Add(prop);
            }
        }
    }
}
