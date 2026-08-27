using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter;

/// <summary>
/// Finds <c>${name}</c> references left pointing at variables the converter could not emit, and
/// stands in a placeholder for each.
///
/// Coralogix rejects a dashboard containing an undefined variable reference — one unconvertible
/// variable therefore takes the whole dashboard down. A placeholder keeps the rest of the
/// dashboard usable and leaves an obvious thing for someone to populate; the alternative is
/// losing every widget on it.
/// </summary>
public static class DanglingVariableReferences
{
    private static readonly Regex ReferencePattern = new(
        @"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    /// <summary>Placeholder value. Matches anything where the reference is used as a regex.</summary>
    private const string PlaceholderValue = ".*";

    /// <summary>
    /// Adds a placeholder variable for every dangling reference and returns their names.
    /// </summary>
    public static IReadOnlyList<string> Fill(JObject dashboard)
    {
        var variables = dashboard["variablesV2"] as JArray;
        if (variables is null)
            return [];

        var defined = variables
            .Children<JObject>()
            .Select(v => v.Value<string>("name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.Ordinal)!;

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        CollectReferences(dashboard["layout"], referenced);
        CollectReferences(dashboard["annotations"], referenced);

        var dangling = referenced
            // Grafana built-ins such as ${__interval} are resolved by Coralogix itself.
            .Where(name => !name.StartsWith("__", StringComparison.Ordinal))
            .Where(name => !defined.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        foreach (var name in dangling)
            variables.Add(BuildPlaceholder(name));

        return dangling;
    }

    private static void CollectReferences(JToken? token, HashSet<string> sink)
    {
        switch (token)
        {
            case JValue { Type: JTokenType.String } value:
                foreach (Match match in ReferencePattern.Matches(value.Value<string>() ?? string.Empty))
                    sink.Add(match.Groups["name"].Value);
                break;

            case JObject obj:
                foreach (var property in obj.Properties())
                    CollectReferences(property.Value, sink);
                break;

            case JArray array:
                foreach (var item in array)
                    CollectReferences(item, sink);
                break;
        }
    }

    private static JObject BuildPlaceholder(string name) => new()
    {
        ["name"] = name,
        ["displayName"] = name,
        ["displayType"] = "VARIABLE_DISPLAY_TYPE_V2_LABEL_VALUE",
        ["source"] = new JObject
        {
            ["static"] = new JObject
            {
                ["values"] = new JArray(new JObject
                {
                    ["value"] = PlaceholderValue,
                    ["label"] = PlaceholderValue
                }),
                ["valuesOrderDirection"] = "ORDER_DIRECTION_ASC",
                ["allOption"] = new JObject { ["includeAll"] = true }
            }
        },
        ["value"] = new JObject { ["multiString"] = new JObject { ["all"] = new JObject() } },
        ["displayFullRow"] = false,
        ["id"] = new JObject { ["value"] = Guid.NewGuid().ToString() }
    };
}
