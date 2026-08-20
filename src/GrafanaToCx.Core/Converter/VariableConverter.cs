using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter;

public sealed class VariableConverter
{
    private static readonly HashSet<string> SkipVariableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "quantile_stat",
        "DS_PROMETHEUS"
    };

    private readonly ILogger _logger;

    public VariableConverter(ILogger logger)
    {
        _logger = logger;
    }

    public JArray ConvertVariables(JArray grafanaVariables, ISet<string> discoveredMetrics)
    {
        var result = new JArray();
        var sourceMetric = ResolveSourceMetric(discoveredMetrics);

        foreach (var varToken in grafanaVariables.Children<JObject>())
        {
            try
            {
                var converted = ConvertVariable(varToken, sourceMetric);
                if (converted != null)
                {
                    result.Add(converted);
                }
            }
            catch (Exception ex)
            {
                // Warning, not Debug: a dropped variable leaves every ${name} reference in the dashboard's
                // queries unresolved, so it must not disappear quietly.
                _logger.LogWarning(
                    "Dropped dashboard variable '{Variable}': {Error}",
                    varToken.Value<string>("name") ?? "(unnamed)",
                    ex.Message);
            }
        }

        result.Add(BuildIntervalVariable());
        return result;
    }

    private JObject? ConvertVariable(JObject varToken, string sourceMetric)
    {
        var name = varToken.Value<string>("name") ?? string.Empty;
        var varType = varToken.Value<string>("type") ?? string.Empty;

        if (varType is "datasource" or "adhoc")
        {
            return null;
        }

        if (SkipVariableNames.Contains(name))
        {
            return null;
        }

        var queryDef = ExtractQueryDefinition(varToken["query"]);

        if (queryDef.Contains("metrics(", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return varType switch
        {
            "query" => ConvertQueryVariable(varToken, name, queryDef, sourceMetric),
            "interval" => ConvertIntervalVariable(varToken, name),
            "constant" => ConvertConstantVariable(varToken, name, queryDef),
            "custom" => ConvertCustomVariable(varToken, name),
            _ => null
        };
    }

    private static JObject? ConvertQueryVariable(JObject varToken, string name, string queryDef, string sourceMetric)
    {
        if (queryDef.TrimStart().StartsWith('{'))
        {
            var esTermsVariable = ConvertElasticsearchTermsQueryVariable(varToken, name, queryDef);
            return esTermsVariable ?? ConvertQueryVariableToStaticFallback(varToken, name, queryDef);
        }

        if (!queryDef.Contains("label_values", StringComparison.OrdinalIgnoreCase))
        {
            return ConvertQueryVariableToStaticFallback(varToken, name, queryDef);
        }

        var metricName = sourceMetric;
        var labelName = name;

        var oneArg = Regex.Match(queryDef, @"label_values\((\w+)\)");
        if (oneArg.Success)
        {
            labelName = oneArg.Groups[1].Value;
        }

        var twoArgs = Regex.Match(queryDef, @"label_values\((\w+),\s*(\w+)\)");
        if (twoArgs.Success)
        {
            metricName = twoArgs.Groups[1].Value;
            labelName = twoArgs.Groups[2].Value;
        }

        var includeAll = varToken.Value<bool?>("includeAll") ?? false;
        var multi = varToken.Value<bool?>("multi") ?? false;
        var current = varToken["current"] as JObject ?? new JObject();

        // A multi-select variable stores current.value as an array. Reading it with Value<string> throws,
        // and the caller's catch turned that into a silently dropped variable — leaving every ${var}
        // reference in the dashboard's queries dangling.
        var currentValueToken = current["value"];
        var currentValue = ExtractSingleValue(currentValueToken);
        var currentLabel = ExtractSingleValue(current["text"]);
        if (string.IsNullOrWhiteSpace(currentLabel))
            currentLabel = currentValue;

        var useMulti = includeAll || multi
            || currentValueToken is JArray
            || string.IsNullOrEmpty(currentValue)
            || currentValue == "$__all";

        return new JObject
        {
            ["name"] = name,
            ["displayName"] = varToken.Value<string>("label") ?? name,
            ["displayType"] = "VARIABLE_DISPLAY_TYPE_V2_LABEL_VALUE",
            ["source"] = new JObject
            {
                ["query"] = new JObject
                {
                    ["metricsQuery"] = new JObject
                    {
                        ["type"] = new JObject
                        {
                            ["labelValue"] = new JObject
                            {
                                ["metricName"] = new JObject { ["stringValue"] = metricName },
                                ["labelName"] = new JObject { ["stringValue"] = labelName },
                                ["labelFilters"] = new JArray()
                            }
                        }
                    },
                    ["valuesOrderDirection"] = "ORDER_DIRECTION_ASC",
                    ["refreshStrategy"] = "REFRESH_STRATEGY_UNSPECIFIED",
                    ["valueDisplayOptions"] = new JObject(),
                    ["allOption"] = new JObject { ["includeAll"] = includeAll }
                }
            },
            ["value"] = useMulti
                ? new JObject { ["multiString"] = new JObject { ["all"] = new JObject() } }
                : new JObject
                {
                    ["singleString"] = new JObject
                    {
                        ["value"] = new JObject
                        {
                            ["value"] = currentValue,
                            ["label"] = currentLabel
                        }
                    }
                },
            ["displayFullRow"] = false,
            ["id"] = new JObject { ["value"] = Guid.NewGuid().ToString() }
        };
    }

    private static JObject? ConvertQueryVariableToStaticFallback(JObject varToken, string name, string queryDef)
    {
        var includeAll = varToken.Value<bool?>("includeAll") ?? false;
        var multi = varToken.Value<bool?>("multi") ?? false;
        var current = varToken["current"] as JObject ?? new JObject();
        var staticValues = new JArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var options = varToken["options"] as JArray ?? new JArray();
        foreach (var option in options.Children<JObject>())
        {
            var optionValue = option.Value<string>("value");
            if (string.IsNullOrWhiteSpace(optionValue))
            {
                optionValue = option.Value<string>("text");
            }

            if (string.IsNullOrWhiteSpace(optionValue))
            {
                continue;
            }

            var isSelected = option.Value<bool?>("selected") ?? false;
            AddStaticOption(staticValues, seen, optionValue, isSelected);
        }

        var currentValueToken = current["value"];
        var currentTextToken = current["text"];
        var currentValue = ExtractSingleValue(currentValueToken);
        var currentLabel = ExtractSingleValue(currentTextToken);
        if (string.IsNullOrWhiteSpace(currentLabel))
        {
            currentLabel = currentValue;
        }

        if (staticValues.Count == 0)
        {
            if (currentValueToken is JArray currentValuesArray)
            {
                foreach (var valueToken in currentValuesArray)
                {
                    AddStaticOption(staticValues, seen, valueToken?.ToString());
                }
            }
            else
            {
                AddStaticOption(staticValues, seen, currentValue);
            }
        }

        if (staticValues.Count == 0
            && TryParseQueryAsValueList(queryDef, out var queryValues))
        {
            foreach (var queryValue in queryValues)
            {
                AddStaticOption(staticValues, seen, queryValue);
            }
        }

        if (staticValues.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(currentValue))
        {
            currentValue = staticValues[0]?["value"]?.ToString() ?? string.Empty;
            currentLabel = staticValues[0]?["label"]?.ToString() ?? currentValue;
        }

        var useMulti = includeAll
                       || multi
                       || currentValueToken is JArray
                       || string.IsNullOrEmpty(currentValue)
                       || currentValue == "$__all";

        return new JObject
        {
            ["name"] = name,
            ["displayName"] = varToken.Value<string>("label") ?? name,
            ["displayType"] = "VARIABLE_DISPLAY_TYPE_V2_LABEL_VALUE",
            ["source"] = new JObject
            {
                ["static"] = new JObject
                {
                    ["values"] = staticValues,
                    ["valuesOrderDirection"] = "ORDER_DIRECTION_ASC",
                    ["allOption"] = new JObject { ["includeAll"] = includeAll }
                }
            },
            ["value"] = useMulti
                ? new JObject { ["multiString"] = new JObject { ["all"] = new JObject() } }
                : new JObject
                {
                    ["singleString"] = new JObject
                    {
                        ["value"] = new JObject
                        {
                            ["value"] = currentValue,
                            ["label"] = currentLabel
                        }
                    }
                },
            ["displayFullRow"] = false,
            ["id"] = new JObject { ["value"] = Guid.NewGuid().ToString() }
        };
    }

    private static JObject ConvertIntervalVariable(JObject varToken, string name)
    {
        var current = varToken["current"] as JObject ?? new JObject();
        var currentValue = ExtractSingleValue(current["value"]);
        if (string.IsNullOrWhiteSpace(currentValue))
            currentValue = "5m";

        return new JObject
        {
            ["name"] = name,
            ["displayName"] = varToken.Value<string>("label") ?? name,
            ["displayType"] = "VARIABLE_DISPLAY_TYPE_V2_LABEL_VALUE",
            ["source"] = new JObject
            {
                ["static"] = new JObject
                {
                    ["values"] = new JArray
                    {
                        Option("1m"), Option("5m"), Option("10m"), Option("30m", isDefault: true),
                        Option("1h"), Option("6h"), Option("12h"), Option("1d")
                    },
                    ["valuesOrderDirection"] = "ORDER_DIRECTION_ASC",
                    ["allOption"] = new JObject { ["includeAll"] = false }
                }
            },
            ["value"] = new JObject
            {
                ["singleString"] = new JObject
                {
                    ["value"] = new JObject { ["value"] = currentValue, ["label"] = currentValue }
                }
            },
            ["displayFullRow"] = false,
            ["id"] = new JObject { ["value"] = Guid.NewGuid().ToString() }
        };
    }

    private static JObject? ConvertConstantVariable(JObject varToken, string name, string queryDef)
    {
        var constantValue = varToken.Value<string>("query") ?? queryDef;
        if (string.IsNullOrWhiteSpace(constantValue))
        {
            return null;
        }

        return new JObject
        {
            ["name"] = name,
            ["displayName"] = varToken.Value<string>("label") ?? name,
            ["displayType"] = "VARIABLE_DISPLAY_TYPE_V2_LABEL_VALUE",
            ["source"] = new JObject
            {
                ["static"] = new JObject
                {
                    ["values"] = new JArray { Option(constantValue, isDefault: true) },
                    ["valuesOrderDirection"] = "ORDER_DIRECTION_ASC",
                    ["allOption"] = new JObject { ["includeAll"] = false }
                }
            },
            ["value"] = new JObject
            {
                ["singleString"] = new JObject
                {
                    ["value"] = new JObject { ["value"] = constantValue, ["label"] = constantValue }
                }
            },
            ["displayFullRow"] = false,
            ["id"] = new JObject { ["value"] = Guid.NewGuid().ToString() }
        };
    }

    private static JObject? ConvertElasticsearchTermsQueryVariable(JObject varToken, string name, string queryDef)
    {
        JObject? esQuery;
        try
        {
            esQuery = JObject.Parse(queryDef);
        }
        catch
        {
            return null;
        }

        var find = esQuery.Value<string>("find") ?? string.Empty;
        if (!string.Equals(find, "terms", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fieldName = esQuery.Value<string>("field") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        if (fieldName.EndsWith(".keyword", StringComparison.OrdinalIgnoreCase))
        {
            fieldName = fieldName[..^".keyword".Length];
        }

        var includeAll = varToken.Value<bool?>("includeAll") ?? false;
        var multi = varToken.Value<bool?>("multi") ?? false;
        var current = varToken["current"] as JObject ?? new JObject();

        // Same multi-select array hazard as the metrics path above.
        var currentValueToken = current["value"];
        var currentValue = ExtractSingleValue(currentValueToken);
        var currentLabel = ExtractSingleValue(current["text"]);
        if (string.IsNullOrWhiteSpace(currentLabel))
            currentLabel = currentValue;

        var useMulti = includeAll || multi || currentValueToken is JArray || string.IsNullOrEmpty(currentValue);
        var keypath = new JArray(fieldName.Split('.').Cast<object>().ToArray());

        return new JObject
        {
            ["name"] = name,
            ["displayName"] = varToken.Value<string>("label") ?? name,
            ["displayType"] = "VARIABLE_DISPLAY_TYPE_V2_LABEL_VALUE",
            ["source"] = new JObject
            {
                ["query"] = new JObject
                {
                    ["logsQuery"] = new JObject
                    {
                        ["type"] = new JObject
                        {
                            ["fieldValue"] = new JObject
                            {
                                ["observationField"] = new JObject
                                {
                                    ["keypath"] = keypath,
                                    ["scope"] = "DATASET_SCOPE_USER_DATA"
                                }
                            }
                        }
                    },
                    ["valuesOrderDirection"] = "ORDER_DIRECTION_ASC",
                    ["refreshStrategy"] = "REFRESH_STRATEGY_ON_DASHBOARD_LOAD",
                    ["valueDisplayOptions"] = new JObject(),
                    ["allOption"] = new JObject { ["includeAll"] = includeAll }
                }
            },
            ["value"] = useMulti
                ? new JObject { ["multiString"] = new JObject { ["all"] = new JObject() } }
                : new JObject
                {
                    ["singleString"] = new JObject
                    {
                        ["value"] = new JObject { ["value"] = currentValue, ["label"] = currentLabel }
                    }
                },
            ["displayFullRow"] = false,
            ["id"] = new JObject { ["value"] = Guid.NewGuid().ToString() }
        };
    }

    private static JObject? ConvertCustomVariable(JObject varToken, string name)
    {
        var options = varToken["options"] as JArray ?? new JArray();
        if (options.Count == 0)
        {
            return null;
        }

        var staticValues = new JArray();
        foreach (var option in options.Children<JObject>())
        {
            var val = option.Value<string>("value") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(val)) continue;
            var isSelected = option.Value<bool?>("selected") ?? false;
            staticValues.Add(Option(val, isDefault: isSelected));
        }

        if (staticValues.Count == 0) return null;

        var current = varToken["current"] as JObject ?? new JObject();
        // current["value"] may be a JArray for multi-select variables; avoid casting directly to string
        var currentValueToken = current["value"];
        var currentValue = currentValueToken is JArray arr
            ? arr.FirstOrDefault()?.ToString() ?? string.Empty
            : currentValueToken?.ToString() ?? string.Empty;
        var useMulti = currentValueToken is JArray
                       || string.IsNullOrEmpty(currentValue)
                       || currentValue == "$__all";

        return new JObject
        {
            ["name"] = name,
            ["displayName"] = varToken.Value<string>("label") ?? name,
            ["displayType"] = "VARIABLE_DISPLAY_TYPE_V2_LABEL_VALUE",
            ["source"] = new JObject
            {
                ["static"] = new JObject
                {
                    ["values"] = staticValues,
                    ["valuesOrderDirection"] = "ORDER_DIRECTION_ASC",
                    ["allOption"] = new JObject { ["includeAll"] = false }
                }
            },
            ["value"] = useMulti
                ? new JObject { ["multiString"] = new JObject { ["all"] = new JObject() } }
                : new JObject
                {
                    ["singleString"] = new JObject
                    {
                        ["value"] = new JObject { ["value"] = currentValue, ["label"] = currentValue }
                    }
                },
            ["displayFullRow"] = false,
            ["id"] = new JObject { ["value"] = Guid.NewGuid().ToString() }
        };
    }

    private static JObject BuildIntervalVariable()
    {
        return new JObject
        {
            ["name"] = "interval",
            ["displayName"] = "Interval",
            ["displayType"] = "VARIABLE_DISPLAY_TYPE_V2_LABEL_VALUE",
            ["source"] = new JObject
            {
                ["static"] = new JObject
                {
                    ["values"] = new JArray
                    {
                        Option("5m", isDefault: true), Option("30m"), Option("1h"), Option("12h"), Option("1d")
                    },
                    ["valuesOrderDirection"] = "ORDER_DIRECTION_ASC",
                    ["allOption"] = new JObject { ["includeAll"] = false }
                }
            },
            ["value"] = new JObject
            {
                ["interval"] = new JObject
                {
                    ["value"] = new JObject { ["value"] = "5m" }
                }
            },
            ["displayFullRow"] = false,
            ["id"] = new JObject { ["value"] = Guid.NewGuid().ToString() }
        };
    }

    private static string ResolveSourceMetric(ISet<string> discoveredMetrics)
    {
        foreach (var metric in discoveredMetrics.OrderBy(x => x))
        {
            if (metric.StartsWith("k6_", StringComparison.OrdinalIgnoreCase)
                && !metric.EndsWith("_total", StringComparison.OrdinalIgnoreCase))
            {
                return metric;
            }
        }

        return "k6_vus";
    }

    private static JObject Option(string value, bool isDefault = false)
    {
        var option = new JObject
        {
            ["value"] = value,
            ["label"] = value
        };

        if (isDefault)
        {
            option["isDefault"] = true;
        }

        return option;
    }

    private static void AddStaticOption(JArray target, ISet<string> seen, string? rawValue, bool isDefault = false)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return;
        }

        var normalizedValue = rawValue.Trim();
        if (!seen.Add(normalizedValue))
        {
            return;
        }

        target.Add(Option(normalizedValue, isDefault));
    }

    private static string ExtractSingleValue(JToken? token)
    {
        return token switch
        {
            JArray array => array.FirstOrDefault()?.ToString() ?? string.Empty,
            _ => token?.ToString() ?? string.Empty
        };
    }

    private static bool TryParseQueryAsValueList(string queryDef, out List<string> values)
    {
        values = [];
        if (string.IsNullOrWhiteSpace(queryDef))
        {
            return false;
        }

        if (queryDef.Contains("label_values", StringComparison.OrdinalIgnoreCase)
            || queryDef.Contains("metrics(", StringComparison.OrdinalIgnoreCase)
            || queryDef.Contains('{')
            || queryDef.Contains('(')
            || queryDef.Contains('$'))
        {
            return false;
        }

        foreach (var part in queryDef.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cleaned = part.Trim().Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                values.Add(cleaned);
            }
        }

        return values.Count > 0;
    }

    private static string ExtractQueryDefinition(JToken? queryToken)
    {
        if (queryToken is not JObject queryObject)
        {
            return queryToken?.ToString() ?? string.Empty;
        }

        var nestedQuery = queryObject.Value<string>("query");
        if (!string.IsNullOrWhiteSpace(nestedQuery))
        {
            return nestedQuery;
        }

        // Grafana Elasticsearch variables may store terms definition directly
        // under the query object ({ find, field, ... }) without nested "query".
        return queryObject.ToString();
    }
}
