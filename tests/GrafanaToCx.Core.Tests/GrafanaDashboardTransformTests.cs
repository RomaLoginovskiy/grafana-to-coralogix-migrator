using GrafanaToCx.Core.GrafanaToGrafana;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

public class GrafanaDashboardTransformTests
{
    private static readonly DatasourceIndex TargetDatasources = new(
    [
        new TargetDatasource("cx-logs-uid", "Logs", "elasticsearch", IsDefault: false),
        new TargetDatasource("cx-metrics-uid", "Metrics", "prometheus", IsDefault: true),
        new TargetDatasource("cx-metrics-v2-uid", "Metrics_v2", "prometheus", IsDefault: false),
        new TargetDatasource("cx-traces-uid", "Traces", "tempo", IsDefault: false)
    ]);

    private static GrafanaTransformOptions Options(
        IReadOnlyDictionary<string, string>? overrides = null,
        bool allowDefaultFallback = false,
        string seed = "Team/Dashboard.json") =>
        new()
        {
            Datasources = TargetDatasources,
            DatasourceOverrides = overrides ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            AllowTargetDefaultFallback = allowDefaultFallback,
            UidSeed = seed
        };

    private static GrafanaTransformResult Run(JObject source, GrafanaTransformOptions? options = null) =>
        new GrafanaDashboardTransform().Transform(source, options ?? Options());

    private static JObject Dashboard(params (string Key, JToken Value)[] fields)
    {
        var dashboard = new JObject
        {
            ["title"] = "Alpha",
            ["uid"] = "src-uid",
            ["schemaVersion"] = 38,
            ["panels"] = new JArray()
        };

        foreach (var (key, value) in fields)
            dashboard[key] = value;

        return dashboard;
    }

    // ── Envelope and identity ─────────────────────────────────────────────────

    [Fact]
    public void Transform_ApiEnvelope_UnwrapsDashboardBody()
    {
        var source = new JObject
        {
            ["meta"] = new JObject { ["folderTitle"] = "Team", ["canSave"] = true },
            ["dashboard"] = Dashboard()
        };

        var result = Run(source);

        Assert.Equal("Alpha", result.Title);
        Assert.Null(result.Dashboard["meta"]);
        Assert.Null(result.Dashboard["dashboard"]);
    }

    [Fact]
    public void Transform_RawExport_IsAcceptedUnwrapped() =>
        Assert.Equal("Alpha", Run(Dashboard()).Title);

    [Fact]
    public void Transform_DoesNotMutateSource()
    {
        var source = Dashboard(("id", 4711), ("version", 12));
        var before = source.DeepClone();

        Run(source);

        Assert.True(JToken.DeepEquals(before, source));
    }

    /// <summary>
    /// The source id belongs to the source instance. Grafana looks a dashboard up by numeric id when one
    /// is present, so leaving it in overwrites whatever unrelated dashboard holds that id on the target.
    /// </summary>
    [Fact]
    public void Transform_ForeignNumericId_IsRemovedNotNulled()
    {
        var result = Run(Dashboard(("id", 4711)));

        Assert.False(result.Dashboard.ContainsKey("id"));
        Assert.Contains(result.Diagnostics, d => d.Code == TransformCodes.ForeignIdDropped);
    }

    [Fact]
    public void Transform_ForeignVersionAndIteration_AreRemoved()
    {
        var result = Run(Dashboard(("version", 265), ("iteration", 1699999999999L)));

        Assert.False(result.Dashboard.ContainsKey("version"));
        Assert.False(result.Dashboard.ContainsKey("iteration"));
    }

    [Fact]
    public void Transform_ValidSourceUid_IsPreserved()
    {
        var result = Run(Dashboard());

        Assert.Equal("src-uid", result.Uid);
        Assert.Equal("src-uid", result.Dashboard.Value<string>("uid"));
    }

    [Fact]
    public void Transform_MissingUid_IsDerivedDeterministicallyFromThePath()
    {
        var source = Dashboard();
        source.Remove("uid");

        var first = Run((JObject)source.DeepClone(), Options(seed: "Team/Alpha.json"));
        var second = Run((JObject)source.DeepClone(), Options(seed: "Team/Alpha.json"));
        var other = Run((JObject)source.DeepClone(), Options(seed: "Team/Beta.json"));

        Assert.Equal(first.Uid, second.Uid);
        Assert.NotEqual(first.Uid, other.Uid);
        Assert.StartsWith("g2g-", first.Uid, StringComparison.Ordinal);
        Assert.Contains(first.Diagnostics, d => d.Code == TransformCodes.UidDerivedFromPath);
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("has/slash")]
    [InlineData("aaaaaaaaaabbbbbbbbbbccccccccccddddddddddX")]  // 41 chars
    public void Transform_UidGrafanaWouldReject_IsReplacedWithADerivedOne(string badUid)
    {
        var result = Run(Dashboard(("uid", badUid)));

        Assert.NotEqual(badUid, result.Uid);
        Assert.StartsWith("g2g-", result.Uid, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, d => d.Code == TransformCodes.UidInvalid);
    }

    /// <summary>
    /// Both claimants demote, not just the later one, so identity does not depend on which file the
    /// directory scan happened to reach first.
    /// </summary>
    [Fact]
    public void Transform_ContestedUid_DemotesEveryClaimant()
    {
        var options = new GrafanaTransformOptions
        {
            Datasources = TargetDatasources,
            UidSeed = "Team/Alpha.json",
            ContestedSourceUids = new HashSet<string>(StringComparer.Ordinal) { "src-uid" }
        };

        var result = Run(Dashboard(), options);

        Assert.NotEqual("src-uid", result.Uid);
        Assert.Contains(result.Diagnostics, d => d.Code == TransformCodes.UidContested);
    }

    [Fact]
    public void Transform_EnvelopeFieldsInsideTheBody_AreStrippedAtRoot()
    {
        var result = Run(Dashboard(("folderId", 12), ("folderUid", "abc"), ("slug", "alpha"), ("url", "/d/x")));

        foreach (var key in new[] { "folderId", "folderUid", "slug", "url" })
            Assert.False(result.Dashboard.ContainsKey(key));
    }

    /// <summary>
    /// The prior art removes every property named "meta" at any depth, which destroys Elasticsearch
    /// extended-stats configuration living at panels[].targets[].metrics[].meta.
    /// </summary>
    [Fact]
    public void Transform_NestedMeta_Survives()
    {
        var source = Dashboard(("panels", new JArray
        {
            new JObject
            {
                ["title"] = "P",
                ["targets"] = new JArray
                {
                    new JObject
                    {
                        ["metrics"] = new JArray
                        {
                            new JObject { ["meta"] = new JObject { ["avg"] = true } }
                        }
                    }
                }
            }
        }));

        var result = Run(source);

        Assert.NotNull(result.Dashboard["panels"]![0]!["targets"]![0]!["metrics"]![0]!["meta"]);
    }

    [Fact]
    public void Transform_TitleOverride_ReplacesTheSourceTitle()
    {
        var options = new GrafanaTransformOptions
        {
            Datasources = TargetDatasources,
            UidSeed = "x.json",
            TitleOverride = "Renamed"
        };

        Assert.Equal("Renamed", Run(Dashboard(), options).Title);
    }

    [Fact]
    public void Transform_SchemaVersionBelowFloor_Warns()
    {
        var result = Run(Dashboard(("schemaVersion", 16)));

        Assert.Equal(16, result.SchemaVersion);
        Assert.Equal(16, result.Dashboard.Value<int>("schemaVersion"));
        Assert.Contains(result.Diagnostics, d => d.Code == TransformCodes.SchemaVersionOld);
    }

    // ── Datasources ───────────────────────────────────────────────────────────

    private static JObject WithPanelDatasource(JToken datasource) =>
        Dashboard(("panels", new JArray
        {
            new JObject { ["title"] = "P", ["datasource"] = datasource }
        }));

    private static JToken PanelDatasource(GrafanaTransformResult result) =>
        result.Dashboard["panels"]![0]!["datasource"]!;

    [Fact]
    public void Transform_DatasourceMatchedByName_IsRewrittenToTheTargetUid()
    {
        var result = Run(WithPanelDatasource(
            new JObject { ["type"] = "prometheus", ["uid"] = "src-prom" }));

        // No name on an object ref, so it resolves by type: prometheus is present twice and one is default.
        Assert.Equal("cx-metrics-uid", PanelDatasource(result)["uid"]);
    }

    [Fact]
    public void Transform_ExplicitOverride_BeatsEveryDiscoveryRule()
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src-prom"] = "cx-metrics-v2-uid"
        };

        var result = Run(
            WithPanelDatasource(new JObject { ["type"] = "prometheus", ["uid"] = "src-prom" }),
            Options(overrides));

        Assert.Equal("cx-metrics-v2-uid", PanelDatasource(result)["uid"]);
    }

    /// <summary>
    /// Pre-schemaVersion-33 references are a bare name string. The prior art nulls them, which silently
    /// unbinds the panel.
    /// </summary>
    [Fact]
    public void Transform_LegacyStringDatasource_IsUpgradedToTheObjectForm()
    {
        var result = Run(WithPanelDatasource("Logs"));

        Assert.Equal(JTokenType.Object, PanelDatasource(result).Type);
        Assert.Equal("cx-logs-uid", PanelDatasource(result)["uid"]);
        Assert.Equal("elasticsearch", PanelDatasource(result)["type"]);
    }

    [Fact]
    public void Transform_ExplicitNullDatasource_IsLeftAlone()
    {
        var result = Run(WithPanelDatasource(JValue.CreateNull()));

        Assert.Equal(JTokenType.Null, PanelDatasource(result).Type);
    }

    [Theory]
    [InlineData("$datasource")]
    [InlineData("${datasource}")]
    public void Transform_DatasourceVariableReference_IsNeverResolved(string reference)
    {
        var result = Run(WithPanelDatasource(
            new JObject { ["type"] = "prometheus", ["uid"] = reference }));

        Assert.Equal(reference, PanelDatasource(result)["uid"]);
    }

    [Theory]
    [InlineData("-- Grafana --")]
    [InlineData("grafana")]
    [InlineData("-- Mixed --")]
    [InlineData("-- Dashboard --")]
    public void Transform_BuiltInDatasource_IsPassedThrough(string uid)
    {
        var result = Run(WithPanelDatasource(new JObject { ["type"] = "datasource", ["uid"] = uid }));

        Assert.Equal(uid, PanelDatasource(result)["uid"]);
    }

    /// <summary>
    /// The server-side expression engine, not a backend. Remapping it would repoint a math/reduce/resample
    /// expression at a real datasource and break the panel.
    /// </summary>
    [Fact]
    public void Transform_ExpressionDatasource_IsPassedThroughAndNotReported()
    {
        var result = Run(WithPanelDatasource(new JObject { ["type"] = "__expr__", ["uid"] = "__expr__" }));

        Assert.Equal("__expr__", PanelDatasource(result)["uid"]);
        Assert.Equal("__expr__", PanelDatasource(result)["type"]);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == TransformCodes.DatasourceUnresolved);
    }

    /// <summary>
    /// Coercing an unmatched reference to Prometheus makes a mysql panel query the wrong backend and
    /// return an empty result that looks like "no data". Leaving it puts the error on the panel.
    /// </summary>
    [Fact]
    public void Transform_UnresolvableDatasource_IsLeftUnchangedAndReported()
    {
        var result = Run(WithPanelDatasource(new JObject { ["type"] = "mysql", ["uid"] = "src-mysql" }));

        Assert.Equal("src-mysql", PanelDatasource(result)["uid"]);
        Assert.Equal("mysql", PanelDatasource(result)["type"]);
        Assert.Contains(result.Diagnostics,
            d => d.Code == TransformCodes.DatasourceUnresolved && d.Severity == TransformSeverity.Warn);
    }

    [Fact]
    public void Transform_UnresolvableDatasourceWithDefaultFallbackOn_UsesTheTargetDefault()
    {
        var result = Run(
            WithPanelDatasource(new JObject { ["type"] = "mysql", ["uid"] = "src-mysql" }),
            Options(allowDefaultFallback: true));

        Assert.Equal("cx-metrics-uid", PanelDatasource(result)["uid"]);
    }

    [Fact]
    public void Transform_DatasourceInsideNestedRowPanel_IsRewritten()
    {
        var source = Dashboard(("panels", new JArray
        {
            new JObject
            {
                ["type"] = "row",
                ["title"] = "Row",
                ["panels"] = new JArray
                {
                    new JObject { ["title"] = "Inner", ["datasource"] = "Logs" }
                }
            }
        }));

        var result = Run(source);

        Assert.Equal("cx-logs-uid", result.Dashboard["panels"]![0]!["panels"]![0]!["datasource"]!["uid"]);
    }

    [Fact]
    public void Transform_AnnotationDatasource_IsRewrittenAndTheAnnotationSurvives()
    {
        var source = Dashboard(("annotations", new JObject
        {
            ["list"] = new JArray
            {
                new JObject { ["name"] = "Deploys", ["datasource"] = "Logs" }
            }
        }));

        var result = Run(source);

        var annotation = result.Dashboard["annotations"]!["list"]![0]!;
        Assert.Equal("Deploys", annotation["name"]);
        Assert.Equal("cx-logs-uid", annotation["datasource"]!["uid"]);
    }

    // ── Template variables ────────────────────────────────────────────────────

    private static JObject WithVariable(JObject variable) =>
        Dashboard(("templating", new JObject { ["list"] = new JArray { variable } }));

    /// <summary>
    /// The prior art rewrites every string named "query" anywhere in the document. On a constant variable
    /// that is the literal value, on a datasource variable it is the plugin type filter, and on a query
    /// variable it is often an object.
    /// </summary>
    [Theory]
    [InlineData("constant", "production")]
    [InlineData("custom", "a,b,c")]
    [InlineData("query", "label_values(up, job)")]
    [InlineData("datasource", "prometheus")]
    public void Transform_TemplateVariableQuery_IsNeverRewritten(string type, string query)
    {
        var result = Run(WithVariable(new JObject
        {
            ["name"] = "v",
            ["type"] = type,
            ["query"] = query
        }));

        Assert.Equal(query, result.Dashboard["templating"]!["list"]![0]!["query"]);
    }

    [Fact]
    public void Transform_ObjectValuedTemplateVariableQuery_IsPreserved()
    {
        var query = new JObject { ["query"] = "up", ["refId"] = "A" };
        var result = Run(WithVariable(new JObject
        {
            ["name"] = "v",
            ["type"] = "query",
            ["query"] = query
        }));

        Assert.True(JToken.DeepEquals(query, result.Dashboard["templating"]!["list"]![0]!["query"]));
    }

    /// <summary>
    /// From schemaVersion 33 the pinned value is a uid. Grafana's migrator will not convert a value that
    /// already looks like one, so a stale foreign uid leaves the picker empty.
    /// </summary>
    [Fact]
    public void Transform_DatasourceVariableOnModernSchema_HasItsPinnedSelectionRepointed()
    {
        var result = Run(WithVariable(new JObject
        {
            ["name"] = "ds",
            ["type"] = "datasource",
            ["query"] = "elasticsearch",
            ["current"] = new JObject { ["text"] = "Old Logs", ["value"] = "src-es" },
            ["options"] = new JArray { new JObject { ["value"] = "src-es" } }
        }));

        var variable = result.Dashboard["templating"]!["list"]![0]!;
        Assert.Equal("cx-logs-uid", variable["current"]!["value"]);
        Assert.Equal("Logs", variable["current"]!["text"]);
        Assert.Empty((JArray)variable["options"]!);
        Assert.Equal("elasticsearch", variable["query"]);
    }

    [Fact]
    public void Transform_DatasourceVariableOnLegacySchema_IsLeftToTheTargetMigrator()
    {
        var source = WithVariable(new JObject
        {
            ["name"] = "ds",
            ["type"] = "datasource",
            ["query"] = "prometheus",
            ["current"] = new JObject { ["text"] = "Metrics", ["value"] = "Metrics" }
        });
        source["schemaVersion"] = 32;

        var result = Run(source);

        Assert.Equal("Metrics", result.Dashboard["templating"]!["list"]![0]!["current"]!["value"]);
        Assert.Contains(result.Diagnostics, d => d.Code == TransformCodes.DatasourceVariableLegacy);
    }

    // ── Panels ────────────────────────────────────────────────────────────────

    [Fact]
    public void Transform_LegacyPanelAlert_IsDroppedWithAWarning()
    {
        var result = Run(Dashboard(("panels", new JArray
        {
            new JObject
            {
                ["title"] = "P",
                ["alert"] = new JObject { ["name"] = "old rule" }
            }
        })));

        Assert.Null(result.Dashboard["panels"]![0]!["alert"]);
        Assert.Contains(result.Diagnostics, d => d.Code == TransformCodes.LegacyAlertDropped);
    }

    /// <summary>
    /// A library-panel export carries only the reference, never a body to inline, so deleting the panel
    /// would lose it silently. Keeping it renders a named error box the user can act on.
    /// </summary>
    [Fact]
    public void Transform_LibraryPanelMissingOnTarget_IsKeptAndReported()
    {
        var options = new GrafanaTransformOptions
        {
            Datasources = TargetDatasources,
            UidSeed = "x.json",
            TargetLibraryPanelUids = new HashSet<string>(StringComparer.Ordinal) { "known-lib" }
        };

        var result = Run(Dashboard(("panels", new JArray
        {
            new JObject
            {
                ["gridPos"] = new JObject(),
                ["libraryPanel"] = new JObject { ["uid"] = "absent-lib", ["name"] = "Queue depth" }
            }
        })), options);

        Assert.NotNull(result.Dashboard["panels"]![0]!["libraryPanel"]);
        Assert.Contains(result.Diagnostics, d => d.Code == TransformCodes.LibraryPanelMissing);
    }

    // ── Shared exports ────────────────────────────────────────────────────────

    [Fact]
    public void Transform_SharedExportInputs_AreSubstitutedThenRemoved()
    {
        var source = Dashboard(
            ("__inputs", new JArray
            {
                new JObject
                {
                    ["name"] = "DS_METRICS",
                    ["type"] = "datasource",
                    ["pluginId"] = "prometheus",
                    ["label"] = "Metrics"
                }
            }),
            ("panels", new JArray
            {
                new JObject { ["title"] = "P", ["datasource"] = "${DS_METRICS}" }
            }));

        var result = Run(source);

        Assert.False(result.Dashboard.ContainsKey("__inputs"));
        Assert.Equal("cx-metrics-uid", PanelDatasource(result)["uid"]);
    }

    [Fact]
    public void Transform_UnresolvableInput_LeavesThePlaceholderVisible()
    {
        var source = Dashboard(
            ("__inputs", new JArray
            {
                new JObject
                {
                    ["name"] = "DS_ORACLE",
                    ["type"] = "datasource",
                    ["pluginId"] = "oracle",
                    ["label"] = "Oracle"
                }
            }),
            ("panels", new JArray
            {
                new JObject { ["title"] = "P", ["datasource"] = "${DS_ORACLE}" }
            }));

        var result = Run(source);

        Assert.Equal("${DS_ORACLE}", PanelDatasource(result));
        Assert.Contains(result.Diagnostics, d => d.Code == TransformCodes.InputUnresolved);
    }

    [Fact]
    public void Transform_RequiresBlock_IsRemovedAndMissingPluginsReported()
    {
        var options = new GrafanaTransformOptions
        {
            Datasources = TargetDatasources,
            UidSeed = "x.json",
            TargetPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "timeseries" }
        };

        var result = Run(Dashboard(("__requires", new JArray
        {
            new JObject { ["type"] = "panel", ["id"] = "timeseries" },
            new JObject { ["type"] = "panel", ["id"] = "flant-statusmap-panel" }
        })), options);

        Assert.False(result.Dashboard.ContainsKey("__requires"));
        Assert.Single(result.Diagnostics, d => d.Code == TransformCodes.PluginMissing);
    }

    // ── Fields that must simply survive ───────────────────────────────────────

    [Fact]
    public void Transform_PortableDashboardFields_ArePassedThroughUntouched()
    {
        var source = Dashboard(
            ("refresh", "10s"), ("timezone", "utc"), ("weekStart", "monday"), ("liveNow", true),
            ("graphTooltip", 1), ("editable", false), ("tags", new JArray("a", "b")),
            ("links", new JArray(new JObject { ["url"] = "https://example.com" })),
            ("time", new JObject { ["from"] = "now-6h", ["to"] = "now" }));

        var result = Run(source);

        Assert.Equal("10s", result.Dashboard["refresh"]);
        Assert.Equal("utc", result.Dashboard["timezone"]);
        Assert.Equal("monday", result.Dashboard["weekStart"]);
        Assert.Equal(true, result.Dashboard["liveNow"]);
        Assert.Equal(1, result.Dashboard["graphTooltip"]);
        Assert.Equal(false, result.Dashboard["editable"]);
        Assert.Equal(2, ((JArray)result.Dashboard["tags"]!).Count);
        Assert.Equal("https://example.com", result.Dashboard["links"]![0]!["url"]);
        Assert.Equal("now-6h", result.Dashboard["time"]!["from"]);
    }

    [Fact]
    public void Transform_AbsentOptionalFields_AreNotInjected()
    {
        var result = Run(Dashboard());

        foreach (var key in new[] { "weekStart", "liveNow", "refresh", "links", "tags" })
            Assert.False(result.Dashboard.ContainsKey(key));
    }

    /// <summary>
    /// The direct executable statement of "stable uid plus overwrite means re-running changes nothing".
    /// Fails loudly if a future rule mutates on every pass.
    /// </summary>
    [Fact]
    public void Transform_IsIdempotent()
    {
        var source = Dashboard(
            ("id", 4711), ("version", 3), ("iteration", 1L),
            ("panels", new JArray
            {
                new JObject { ["title"] = "P", ["datasource"] = "Logs" }
            }));

        var once = Run(source);
        var twice = Run((JObject)once.Dashboard.DeepClone());

        Assert.Equal(once.Uid, twice.Uid);
        Assert.True(JToken.DeepEquals(once.Dashboard, twice.Dashboard));
    }
}
