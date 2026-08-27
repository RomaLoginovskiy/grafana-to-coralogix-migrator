using GrafanaToCx.Core.Converter.PanelConverters;
using GrafanaToCx.Core.Converter.Transformations;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter;

public sealed class GrafanaToCxConverter : IGrafanaToCxConverter
{
    private static readonly HashSet<string> DirectPanelTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "stat", "singlestat", "gauge", "bargauge", "text", "table", "logs", "piechart", "barchart"
    };

    private static readonly HashSet<string> AllowedFallbackPanelTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "timeseries", "graph"
    };

    /// <summary>
    /// Grafana chrome — panels carrying no user-authored content. Dropped silently rather
    /// than leaving a "not migrated" placeholder, since there is nothing to miss.
    /// </summary>
    private static readonly HashSet<string> ChromePanelTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "welcome", "dashlist", "news"
    };

    /// <summary>
    /// Transformation ids the converter actually applies. Everything else is recorded as a
    /// dashboard-level loss. Empty today: no planner reads a transformation id.
    /// </summary>
    private static readonly HashSet<string> ImplementedTransformationIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Panel types eligible for fan-out. Restricted to the stat family, where Grafana already
    /// draws one tile per query so N widgets is what the user was looking at. Deliberately
    /// excludes table (its queries are joined by a transformation into one view), piechart
    /// (its queries are slices of one chart) and bargauge (buckets of one distribution).
    /// </summary>
    private static readonly HashSet<string> FanOutPanelTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "stat", "singlestat"
    };
    private const string StatusHistoryPanelType = "status-history";

    private static readonly string[] SectionColors =
    [
        "SECTION_PREDEFINED_COLOR_UNSPECIFIED",
        "SECTION_PREDEFINED_COLOR_BLUE",
        "SECTION_PREDEFINED_COLOR_GREEN",
        "SECTION_PREDEFINED_COLOR_PURPLE",
        "SECTION_PREDEFINED_COLOR_PINK",
        "SECTION_PREDEFINED_COLOR_CYAN",
        "SECTION_PREDEFINED_COLOR_MAGENTA",
        "SECTION_PREDEFINED_COLOR_ORANGE"
    ];

    private readonly ILogger<GrafanaToCxConverter> _logger;
    private readonly LineChartPanelConverter _lineChartConverter = new();
    private readonly GaugePanelConverter _gaugeConverter = new();
    private readonly MarkdownPanelConverter _markdownConverter = new();
    private readonly LogsPanelConverter _logsPanelConverter = new();
    private readonly PieChartPanelConverter _pieChartConverter;
    private readonly BarChartPanelConverter _barChartConverter = new();
    private readonly DataTablePanelConverter _dataTableConverter = new();
    private readonly CompositeTransformationPlanner _transformationPlanner;
    private readonly List<PanelConversionDiagnostic> _conversionDiagnostics = [];
    private readonly List<DashboardConversionDiagnostic> _dashboardDiagnostics = [];
    private readonly List<JObject> _conversionDecisionEvents = [];
    private readonly HashSet<string> _honouredRepeatPanels = new(StringComparer.Ordinal);
    private readonly List<JObject> _thresholdAnnotations = [];

    public IReadOnlyList<PanelConversionDiagnostic> ConversionDiagnostics => _conversionDiagnostics;
    public IReadOnlyList<DashboardConversionDiagnostic> DashboardDiagnostics => _dashboardDiagnostics;
    public IReadOnlyList<JObject> ConversionDecisionEvents => _conversionDecisionEvents;

    public GrafanaToCxConverter(
        ILogger<GrafanaToCxConverter> logger,
        MultiLuceneMergeOptions? mergeOptions = null)
    {
        _logger = logger;
        _transformationPlanner = new CompositeTransformationPlanner(mergeOptions ?? MultiLuceneMergeOptions.Disabled);
        _pieChartConverter = new PieChartPanelConverter(AddDiagnostic);
    }

    public string Convert(string grafanaJson, ConversionOptions? options = null)
    {
        var result = ConvertToJObject(grafanaJson, options);
        return result.ToString(Formatting.Indented);
    }

    public JObject ConvertToJObject(string grafanaJson, ConversionOptions? options = null)
    {
        _conversionDiagnostics.Clear();
        _dashboardDiagnostics.Clear();
        _conversionDecisionEvents.Clear();
        _honouredRepeatPanels.Clear();
        _thresholdAnnotations.Clear();
        var sourceToken = JToken.Parse(grafanaJson);
        var sourceObject = sourceToken as JObject ?? new JObject();
        var grafana = sourceObject["dashboard"] as JObject ?? sourceObject;

        var discoveredMetrics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var customDashboard = InitializeDashboard(grafana, options);
        RecordDashboardLevelLosses(grafana);
        ConvertPanels(grafana, customDashboard, discoveredMetrics, options);
        customDashboard["annotations"] = new JArray(_thresholdAnnotations);
        ConvertVariables(grafana, customDashboard, discoveredMetrics);
        ApplyTimeFrame(grafana, customDashboard);
        // A reference to a variable we could not convert would have the API reject the whole
        // dashboard, so stand in a placeholder and say so.
        foreach (var name in DanglingVariableReferences.Fill(customDashboard))
        {
            AddDashboardDiagnostic(new DashboardConversionDiagnostic(
                "variable",
                name,
                "Referenced by a query but not convertible; a placeholder variable was added so "
                + "the dashboard loads. Populate it to restore the original filtering.",
                DashboardDiagnosticCodes.Variable,
                Outcome: "placeholder"));
        }

        // Runs last: it needs the converted variables to know which are multi-value.
        foreach (var name in PromqlVariableMatchers.Normalize(customDashboard).Distinct())
        {
            AddDashboardDiagnostic(new DashboardConversionDiagnostic(
                "queryMatcher",
                $"${{{name}}}",
                "Trailing '.*' dropped from a variable matcher: it cannot survive unquoting, so a "
                + "prefix match became an exact match.",
                DashboardDiagnosticCodes.QueryMatcher,
                Outcome: "degraded"));
        }

        return customDashboard;
    }

    /// <summary>
    /// Records dashboard-wide elements the converter has no target for. These have no
    /// corresponding read anywhere in conversion, so without this they vanish unreported.
    /// </summary>
    private void RecordDashboardLevelLosses(JObject grafana)
    {
        foreach (var annotation in (grafana["annotations"]?["list"] as JArray ?? []).Children<JObject>())
        {
            // builtIn 1 is Grafana's own "Annotations & Alerts" entry, on every dashboard.
            if (annotation.Value<int?>("builtIn") == 1)
                continue;

            AddDashboardDiagnostic(new DashboardConversionDiagnostic(
                "annotation",
                annotation.Value<string>("name") ?? "(unnamed)",
                "Annotation queries are not emitted; event overlays will be absent.",
                DashboardDiagnosticCodes.Annotation));
        }

        foreach (var link in (grafana["links"] as JArray ?? []).Children<JObject>())
        {
            AddDashboardDiagnostic(new DashboardConversionDiagnostic(
                "dashboardLink",
                link.Value<string>("title") is { Length: > 0 } t ? t : $"({link.Value<string>("type")})",
                "Dashboard links are not emitted.",
                DashboardDiagnosticCodes.DashboardLink));
        }
    }

    /// <summary>
    /// Records per-panel elements that are read past but never applied.
    /// </summary>
    private void RecordPanelLevelLosses(JObject panel, string panelTitle)
    {
        var visibleTargets = VisibleTargetSelector.Resolve(panel["targets"] as JArray ?? []);

        foreach (var transformation in TransformationContext.GetTransformations(panel).Children<JObject>())
        {
            var id = transformation.Value<string>("id") ?? "(unnamed)";
            if (ImplementedTransformationIds.Contains(id))
                continue;

            var reason = DescribeTransformationLoss(id, visibleTargets);
            if (reason is null)
                continue;

            AddDashboardDiagnostic(new DashboardConversionDiagnostic(
                "transformation",
                id,
                reason,
                DashboardDiagnosticCodes.Transformation,
                panelTitle));
        }

        if (panel["links"] is JArray links && links.Count > 0)
        {
            AddDashboardDiagnostic(new DashboardConversionDiagnostic(
                "panelLink",
                $"{links.Count} link(s)",
                "Panel links are not emitted.",
                DashboardDiagnosticCodes.PanelLink,
                panelTitle));
        }

        if (panel.Value<string>("repeat") is { Length: > 0 } repeat
            && !_honouredRepeatPanels.Contains(PanelIdentity(panel)))
        {
            AddDashboardDiagnostic(new DashboardConversionDiagnostic(
                "panelRepeat",
                $"${repeat}",
                "Panel repeat is not expanded: no multi-value variable of that name exists, "
                + "so one widget is emitted instead of one per value.",
                DashboardDiagnosticCodes.PanelRepeat,
                panelTitle));
        }
    }

    private static JObject InitializeDashboard(JObject grafana, ConversionOptions? options)
    {
        var name = options?.DashboardName
                   ?? grafana.Value<string>("title")
                   ?? "Imported Grafana Dashboard";

        return new JObject
        {
            ["id"] = Guid.NewGuid().ToString("N")[..21],
            ["name"] = name,
            ["description"] = grafana.Value<string>("description") ?? string.Empty,
            ["layout"] = new JObject { ["sections"] = new JArray() },
            ["variables"] = new JArray(),
            ["variablesV2"] = new JArray(),
            ["filters"] = new JArray(),
            ["relativeTimeFrame"] = "3600s",
            ["annotations"] = new JArray(),
            ["off"] = new JObject(),
            ["actions"] = new JArray()
        };
    }

    private void ConvertPanels(JObject grafana, JObject customDashboard, ISet<string> discoveredMetrics, ConversionOptions? options)
    {
        var panels = grafana["panels"] as JArray ?? new JArray();
        var repeatableVariables = ResolveRepeatableVariableNames(grafana);
        var sections = GroupPanelsIntoSections(panels);

        if (sections.Count == 0 && panels.Count > 0)
        {
            var fallback = panels.Children<JObject>().Where(p => p.Value<string>("type") != "row").ToList();
            sections.Add((null, fallback));
        }

        var outputSections = (JArray)customDashboard["layout"]!["sections"]!;
        var colorIndex = 0;

        foreach (var (title, sectionPanels) in sections.Where(s => s.panels.Count > 0))
        {
            foreach (var chunk in SplitOutRepeatingPanels(sectionPanels, title, repeatableVariables))
            {
                outputSections.Add(CreateSection(
                    chunk.Panels, chunk.Title, colorIndex, discoveredMetrics, options, chunk.RepeatVariable));
                colorIndex++;
            }
        }
    }

    private static List<(string? title, List<JObject> panels)> GroupPanelsIntoSections(JArray panels)
    {
        var sections = new List<(string? title, List<JObject> panels)>();
        var currentTitle = (string?)null;
        var currentPanels = new List<JObject>();

        foreach (var panelToken in panels)
        {
            if (panelToken is not JObject panel)
            {
                continue;
            }

            var type = panel.Value<string>("type") ?? string.Empty;
            if (type == "row")
            {
                if (currentPanels.Count > 0 || currentTitle != null)
                {
                    sections.Add((currentTitle, currentPanels));
                    currentPanels = new List<JObject>();
                }

                currentTitle = panel.Value<string>("title");

                // When a row is collapsed Grafana stores its child panels inside
                // the row panel's own "panels" array instead of at the top level.
                if (panel.Value<bool?>("collapsed") == true)
                {
                    var nestedPanels = panel["panels"] as JArray ?? new JArray();
                    foreach (var nested in nestedPanels.Children<JObject>())
                    {
                        if (!string.Equals(
                                nested.Value<string>("type"), "row",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            currentPanels.Add(nested);
                        }
                    }
                }

                continue;
            }

            currentPanels.Add(panel);
        }

        if (currentPanels.Count > 0 || currentTitle != null)
        {
            sections.Add((currentTitle, currentPanels));
        }

        return sections;
    }

    private JObject CreateSection(
        IReadOnlyList<JObject> panels,
        string? sectionTitle,
        int colorIndex,
        ISet<string> discoveredMetrics,
        ConversionOptions? options,
        string? repeatVariable = null)
    {
        const int maxWidgetsPerRow = 3;
        var rows = new JArray();
        var currentWidgets = new List<JObject>();

        foreach (var panel in panels)
        {
            var panelType = panel.Value<string>("type") ?? string.Empty;
            if (panelType == "row")
            {
                continue;
            }

            if (panelType == "text")
            {
                FlushWidgets(currentWidgets, rows);
                var markdownWidget = ConvertPanelToWidget(panel, discoveredMetrics, options);
                if (markdownWidget != null)
                {
                    rows.Add(CreateRow(new List<JObject> { markdownWidget }, MarkdownPanelConverter.CalculateHeight(panel)));
                }

                continue;
            }

            if (TryFanOutPanel(panel, options, out var fanOutPanels))
            {
                // Record against the original panel exactly once. The clones must not report,
                // or a five-way fan-out would report the same transformation five times and
                // the repeat — which belongs to the panel, not a slice — not at all.
                RecordPanelLevelLosses(panel, ResolvePanelTitle(panel));

                // Keep the group on its own row(s) so it still reads as one panel would have.
                FlushWidgets(currentWidgets, rows);

                foreach (var clone in fanOutPanels)
                {
                    if (currentWidgets.Count >= maxWidgetsPerRow)
                        FlushWidgets(currentWidgets, rows);

                    var fanOutWidget = ConvertPanelToWidget(
                        clone, discoveredMetrics, options, recordPanelLevelLosses: false);
                    if (fanOutWidget != null)
                        currentWidgets.Add(fanOutWidget);
                }

                FlushWidgets(currentWidgets, rows);
                continue;
            }

            if (currentWidgets.Count >= maxWidgetsPerRow)
            {
                FlushWidgets(currentWidgets, rows);
            }

            var widget = ConvertPanelToWidget(panel, discoveredMetrics, options);
            if (widget != null)
            {
                CollectThresholdAnnotations(panel, widget);
                currentWidgets.Add(widget);
            }
        }

        FlushWidgets(currentWidgets, rows);

        var sectionOptions = BuildSectionOptions(sectionTitle, colorIndex, repeatVariable);

        return new JObject
        {
            ["id"] = WidgetHelpers.IdObject(),
            ["rows"] = rows,
            ["options"] = sectionOptions
        };
    }

    /// <summary>
    /// Splits a multi-query stat panel into one single-query panel per target. Each clone runs
    /// through the normal pipeline, so the planner sees one target and reports no degradation.
    /// Returns false — leaving the panel untouched — unless fan-out is enabled and applicable.
    /// </summary>
    private static bool TryFanOutPanel(
        JObject panel,
        ConversionOptions? options,
        out IReadOnlyList<JObject> fanOutPanels)
    {
        fanOutPanels = [];

        if (options?.FanOutMultiQueryPanels != true)
            return false;

        var panelType = PanelTypes.Normalize(panel.Value<string>("type"));
        if (!FanOutPanelTypes.Contains(panelType))
            return false;

        var visibleTargets = VisibleTargetSelector.Resolve(panel["targets"] as JArray ?? []);
        if (visibleTargets.Count < 2)
            return false;

        var panelTitle = ResolvePanelTitle(panel);

        var clones = new List<JObject>(visibleTargets.Count);
        for (var i = 0; i < visibleTargets.Count; i++)
        {
            var target = visibleTargets[i];
            var clone = (JObject)panel.DeepClone();
            clone["targets"] = new JArray(target.DeepClone());
            clone["title"] = $"{panelTitle} — {DescribeTarget(target, i)}";
            // The repeat belongs to the original panel, not to each slice of it.
            clone.Remove("repeat");
            clones.Add(clone);
        }

        fanOutPanels = clones;
        return true;
    }

    /// <summary>
    /// Names one slice of a fanned-out panel. Grafana labels these tiles with the target alias,
    /// so that is the closest thing to what the user already reads on the panel.
    /// </summary>
    private static string DescribeTarget(JObject target, int index)
    {
        if (target.Value<string>("alias") is { Length: > 0 } alias)
            return alias;
        if (target.Value<string>("legendFormat") is { Length: > 0 } legend)
            return legend;
        if (target.Value<string>("refId") is { Length: > 0 } refId)
            return refId;
        return $"query {index + 1}";
    }

    /// <summary>
    /// Transformations that combine several result frames into one. On a panel with a single
    /// Elasticsearch query there is one frame, so nothing is joined and nothing is lost.
    /// </summary>
    private static readonly HashSet<string> FrameJoiningTransformations = new(StringComparer.OrdinalIgnoreCase)
    {
        "merge", "joinByField"
    };

    /// <summary>
    /// Says what a transformation costs this panel, or null when it costs nothing. Grafana
    /// transformations run over query <em>results</em>; Coralogix has no equivalent stage, so
    /// none are applied — but reporting every one as a loss overstates the damage, and the
    /// point of these diagnostics is that a reader can trust them.
    /// </summary>
    private static string? DescribeTransformationLoss(string id, IReadOnlyList<JObject> visibleTargets)
    {
        if (FrameJoiningTransformations.Contains(id))
        {
            if (visibleTargets.Count > 1)
            {
                return "Transformation joins several query results, but the widget carries a single "
                       + "query — the joined view cannot be reproduced.";
            }

            // A single Prometheus query still returns one frame per series, which this would
            // have combined. A single Elasticsearch query returns one frame: nothing to join.
            if (visibleTargets.Count == 0 || !IsPrometheusTarget(visibleTargets[0]))
                return null;
        }

        return "Transformation is not applied; the widget shows untransformed query results.";
    }

    private static bool IsPrometheusTarget(JObject target)
    {
        var datasourceType = target["datasource"]?["type"]?.ToString();
        if (string.Equals(datasourceType, "prometheus", StringComparison.OrdinalIgnoreCase))
            return true;

        return target["expr"] is JValue;
    }

    private static string ResolvePanelTitle(JObject panel) =>
        panel.Value<string>("title") is { Length: > 0 } title
            ? title
            : $"Panel #{panel.Value<int>("id")}";

    private static void FlushWidgets(List<JObject> widgets, JArray rows)
    {
        if (widgets.Count == 0)
        {
            return;
        }

        rows.Add(CreateRow(widgets));
        widgets.Clear();
    }

    private static JObject CreateRow(List<JObject> widgets, int height = 19)
    {
        return new JObject
        {
            ["id"] = WidgetHelpers.IdObject(),
            ["appearance"] = new JObject { ["height"] = height },
            ["widgets"] = new JArray(widgets)
        };
    }

    private JObject? ConvertPanelToWidget(
        JObject panel,
        ISet<string> discoveredMetrics,
        ConversionOptions? options,
        bool recordPanelLevelLosses = true)
    {
        // Dispatch on the canonical type so legacy identifiers reach the modern converter,
        // but report the raw type — a diagnostic naming "piechart" for a panel the user
        // authored as "grafana-piechart-panel" would be a lie.
        var rawPanelType = panel.Value<string>("type") ?? string.Empty;
        var panelType = PanelTypes.Normalize(rawPanelType);
        var panelTitle = ResolvePanelTitle(panel);

        var targets = panel["targets"] as JArray ?? new JArray();
        var transformations = TransformationContext.GetTransformations(panel);
        if (recordPanelLevelLosses)
            RecordPanelLevelLosses(panel, panelTitle);
        var plan = _transformationPlanner.Plan(new TransformationContext(panel, targets, transformations));

        if (plan is TransformationPlan.Failure failure)
        {
            AddDiagnostic(new PanelConversionDiagnostic(
                panelTitle,
                rawPanelType,
                "error",
                failure.Reason,
                failure.Code,
                failure.DroppedSemantics,
                failure.Approximation,
                failure.ConfidenceScore));
            return MarkdownPanelConverter.CreateErrorWidget(panelTitle, rawPanelType, failure.Reason);
        }

        if (plan is TransformationPlan.Success { Decision: not null } plannedDecision)
        {
            AddDiagnostic(new PanelConversionDiagnostic(
                panelTitle,
                rawPanelType,
                plannedDecision.Decision.Outcome,
                plannedDecision.Decision.Reason,
                plannedDecision.Decision.Code,
                plannedDecision.Decision.DroppedSemantics,
                plannedDecision.Decision.Approximation,
                plannedDecision.Decision.ConfidenceScore));
        }

        JObject? widget = panelType switch
        {
            "stat" or "singlestat" or "gauge" or "bargauge" => _gaugeConverter.Convert(panel, discoveredMetrics, plan),
            "text" => _markdownConverter.Convert(panel, discoveredMetrics, plan),
            "table" => _dataTableConverter.Convert(panel, discoveredMetrics, plan),
            "logs" => _logsPanelConverter.Convert(panel, discoveredMetrics, plan),
            "piechart" => _pieChartConverter.Convert(panel, discoveredMetrics, plan),
            "barchart" => _barChartConverter.Convert(panel, discoveredMetrics, plan),
            "timeseries" or "graph" => _lineChartConverter.Convert(panel, discoveredMetrics, plan),
            _ => null
        };

        if (widget != null)
        {
            if (AllowedFallbackPanelTypes.Contains(panelType))
            {
                AddDiagnostic(new PanelConversionDiagnostic(
                    panelTitle,
                    rawPanelType,
                    "fallback",
                    "Converted with lineChart fallback.",
                    "DGR-LIN-001",
                    [],
                    "linechart-fallback",
                    0.9));
            }

            return widget;
        }

        if (DirectPanelTypes.Contains(panelType) || AllowedFallbackPanelTypes.Contains(panelType))
        {
            AddDiagnostic(new PanelConversionDiagnostic(
                panelTitle,
                rawPanelType,
                "skipped",
                "Panel converter produced no widget (empty/hidden/invalid targets).",
                "UNS-TGT-001",
                [],
                "none",
                1.0));
            return null;
        }

        if (string.Equals(panelType, StatusHistoryPanelType, StringComparison.OrdinalIgnoreCase) &&
            TryConvertShapeBasedFallback(panel, panelType, panelTitle, discoveredMetrics, plan, out var shapeFallbackWidget))
        {
            return shapeFallbackWidget;
        }

        if (options?.SkipUnsupportedPanels ?? true)
        {
            AddDiagnostic(new PanelConversionDiagnostic(
                panelTitle,
                rawPanelType,
                "skipped",
                "Unsupported Grafana panel type.",
                "UNS-PNL-001",
                [],
                "none",
                1.0));

            // A panel that showed real data leaves a marker so the absence is visible on the
            // dashboard; Grafana's own chrome carries nothing to miss and goes quietly.
            return ChromePanelTypes.Contains(panelType)
                ? null
                : MarkdownPanelConverter.CreateNotMigratedWidget(panelTitle, rawPanelType);
        }

        if (TryConvertShapeBasedFallback(panel, panelType, panelTitle, discoveredMetrics, plan, out var unsupportedFallbackWidget))
        {
            return unsupportedFallbackWidget;
        }

        return null;
    }

    private bool TryConvertShapeBasedFallback(
        JObject panel,
        string panelType,
        string panelTitle,
        ISet<string> discoveredMetrics,
        TransformationPlan plan,
        out JObject? widget)
    {
        widget = null;
        var fallback = SelectShapeFallback(panelType, panel, plan);
        if (fallback == null)
            return false;

        widget = fallback.WidgetType switch
        {
            "lineChart" => _lineChartConverter.Convert(panel, discoveredMetrics, plan),
            "barChart" => _barChartConverter.Convert(panel, discoveredMetrics, plan),
            "dataTable" => _dataTableConverter.Convert(panel, discoveredMetrics, plan),
            _ => null
        };

        if (widget == null)
            return false;

        AddDiagnostic(new PanelConversionDiagnostic(
            panelTitle,
            panelType,
            "fallback",
            fallback.Reason,
            fallback.Code,
            [],
            fallback.Approximation,
            fallback.ConfidenceScore));
        return true;
    }

    private static ShapeFallbackDecision? SelectShapeFallback(string panelType, JObject panel, TransformationPlan plan)
    {
        var targets = PanelTargetSelector.ResolveVisibleTargets(panel, plan);
        if (targets.Count == 0)
            return null;

        var primaryTarget = targets[0];
        var shape = AnalyzeTargetShape(primaryTarget);
        if (string.Equals(panelType, StatusHistoryPanelType, StringComparison.OrdinalIgnoreCase))
        {
            if (shape.IsAggregatedLogs && shape.HasUsableMetric && shape.HasGroupingSignal)
            {
                return new ShapeFallbackDecision(
                    "barChart",
                    "Mapped status-history to barChart from aggregated logs shape (usable metric + grouping signal).",
                    "DGR-STH-001",
                    "shape-barchart",
                    0.86);
            }

            return new ShapeFallbackDecision(
                "dataTable",
                "Mapped status-history to dataTable from ambiguous or record-like query shape.",
                "DGR-STH-002",
                "shape-table",
                0.78);
        }

        if (shape.IsMetricsLike)
        {
            return new ShapeFallbackDecision(
                "lineChart",
                "Selected lineChart fallback from metrics-like target shape.",
                "DGR-SHF-001",
                "shape-linechart",
                0.85);
        }

        if (shape.IsAggregatedLogs && shape.HasUsableMetric && shape.HasGroupingSignal)
        {
            if (shape.HasDateHistogram)
            {
                return new ShapeFallbackDecision(
                    "lineChart",
                    "Selected lineChart fallback from logs shape with date_histogram time bucketing.",
                    "DGR-SHF-002",
                    "shape-linechart",
                    0.82);
            }

            return new ShapeFallbackDecision(
                "barChart",
                "Selected barChart fallback from aggregated logs shape with grouping.",
                "DGR-SHF-003",
                "shape-barchart",
                0.8);
        }

        return new ShapeFallbackDecision(
            "dataTable",
            "Selected dataTable fallback from ambiguous or record-like query shape.",
            "DGR-SHF-004",
            "shape-table",
            0.76);
    }

    private static TargetShape AnalyzeTargetShape(JObject target)
    {
        var dsType = target["datasource"]?["type"]?.ToString();
        var isElasticsearchLike =
            string.Equals(dsType, "elasticsearch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dsType, "opensearch", StringComparison.OrdinalIgnoreCase) ||
            (target["bucketAggs"] is JArray && target["expr"] == null);
        var isMetricsLike = target["expr"] is JValue ||
                            string.Equals(dsType, "prometheus", StringComparison.OrdinalIgnoreCase);

        var bucketAggs = target["bucketAggs"] as JArray ?? [];
        var hasTerms = bucketAggs
            .Children<JObject>()
            .Any(b => string.Equals(b.Value<string>("type"), "terms", StringComparison.OrdinalIgnoreCase));
        var hasDateHistogram = bucketAggs
            .Children<JObject>()
            .Any(b => string.Equals(b.Value<string>("type"), "date_histogram", StringComparison.OrdinalIgnoreCase));
        var hasGroupingSignal = hasTerms || hasDateHistogram;

        var metric = (target["metrics"] as JArray)?.Children<JObject>().FirstOrDefault();
        var metricType = metric?.Value<string>("type") ?? string.Empty;
        var metricField = metric?.Value<string>("field") ?? string.Empty;
        var hasUsableMetric = IsUsableMetric(metricType, metricField);

        return new TargetShape(
            IsMetricsLike: isMetricsLike,
            IsAggregatedLogs: isElasticsearchLike,
            HasDateHistogram: hasDateHistogram,
            HasGroupingSignal: hasGroupingSignal,
            HasUsableMetric: hasUsableMetric);
    }

    private static bool IsUsableMetric(string metricType, string metricField)
    {
        if (string.IsNullOrWhiteSpace(metricType))
            return false;

        if (string.Equals(metricType, "count", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(metricType, "raw_data", StringComparison.OrdinalIgnoreCase))
            return false;

        if (metricType is "sum" or "avg" or "min" or "max")
            return !string.IsNullOrWhiteSpace(metricField);

        return true;
    }

    private sealed record ShapeFallbackDecision(
        string WidgetType,
        string Reason,
        string Code,
        string Approximation,
        double ConfidenceScore);

    private sealed record TargetShape(
        bool IsMetricsLike,
        bool IsAggregatedLogs,
        bool HasDateHistogram,
        bool HasGroupingSignal,
        bool HasUsableMetric);

    private void AddDiagnostic(PanelConversionDiagnostic diagnostic)
    {
        _conversionDiagnostics.Add(diagnostic);
        _conversionDecisionEvents.Add(new JObject
        {
            ["panelTitle"] = diagnostic.PanelTitle,
            ["panelType"] = diagnostic.PanelType,
            ["outcome"] = diagnostic.Outcome,
            ["code"] = diagnostic.Code ?? string.Empty,
            ["reason"] = diagnostic.Reason,
            ["droppedSemantics"] = diagnostic.DroppedSemantics != null
                ? new JArray(diagnostic.DroppedSemantics)
                : new JArray(),
            ["approximation"] = diagnostic.Approximation ?? string.Empty,
            ["confidenceScore"] = diagnostic.ConfidenceScore
        });
    }

    /// <summary>
    /// One chunk of a Grafana row after repeating panels have been separated out. Coralogix
    /// repeats a whole section, so a repeating panel cannot share one with its neighbours.
    /// </summary>
    private sealed record SectionChunk(IReadOnlyList<JObject> Panels, string? Title, string? RepeatVariable);

    /// <summary>
    /// Names of variables a section may repeat over: present on the dashboard and multi-value.
    /// The API does not validate the reference, so a dangling name would fail silently at
    /// render time rather than being caught by the pre-upload check.
    /// </summary>
    private static HashSet<string> ResolveRepeatableVariableNames(JObject grafana)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var variable in (grafana["templating"]?["list"] as JArray ?? []).Children<JObject>())
        {
            var name = variable.Value<string>("name");
            if (!string.IsNullOrEmpty(name) && VariableConverter.WillBeMultiValue(variable))
                names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// Splits a row so that each repeating panel gets a section of its own, preserving document
    /// order. A panel whose repeat variable is missing or single-valued stays where it is and is
    /// reported as a loss.
    /// </summary>
    private IEnumerable<SectionChunk> SplitOutRepeatingPanels(
        IReadOnlyList<JObject> panels,
        string? title,
        IReadOnlySet<string> repeatableVariables)
    {
        var pending = new List<JObject>();

        foreach (var panel in panels)
        {
            var repeat = panel.Value<string>("repeat");
            if (string.IsNullOrEmpty(repeat) || !repeatableVariables.Contains(repeat))
            {
                pending.Add(panel);
                continue;
            }

            if (pending.Count > 0)
            {
                yield return new SectionChunk(pending.ToList(), title, null);
                pending.Clear();
            }

            _honouredRepeatPanels.Add(PanelIdentity(panel));
            yield return new SectionChunk([panel], ResolvePanelTitle(panel), repeat);
        }

        if (pending.Count > 0)
            yield return new SectionChunk(pending, title, null);
    }

    /// <summary>
    /// Grafana draws thresholds as horizontal lines on a time series. Coralogix has no
    /// equivalent on the widget, so they become dashboard annotations scoped to it.
    /// Only line charts: gauges already carry their thresholds natively.
    /// </summary>
    private void CollectThresholdAnnotations(JObject panel, JObject widget)
    {
        if (widget["definition"]?["lineChart"] is null)
            return;

        var widgetId = widget["id"]?["value"]?.ToString();
        if (string.IsNullOrEmpty(widgetId))
            return;

        var annotations = ThresholdAnnotations.Build(panel, widgetId, ResolvePanelTitle(panel));
        if (annotations.Count == 0)
            return;

        _thresholdAnnotations.AddRange(annotations);
    }

    private static string PanelIdentity(JObject panel) =>
        $"{panel.Value<int?>("id")}|{panel.Value<string>("title")}";

    private static JObject BuildSectionOptions(string? sectionTitle, int colorIndex, string? repeatVariable = null)
    {
        // SectionOptions is a oneof: custom XOR internal. A repeating section must therefore
        // use custom, which also means it needs a name.
        if (string.IsNullOrWhiteSpace(sectionTitle) && repeatVariable is null)
        {
            return new JObject { ["internal"] = new JObject() };
        }

        var color = SectionColors[colorIndex % SectionColors.Length];
        var custom = new JObject
        {
            ["name"] = string.IsNullOrWhiteSpace(sectionTitle) ? $"${repeatVariable}" : sectionTitle,
            ["collapsed"] = false,
            ["color"] = new JObject { ["predefined"] = color }
        };

        if (repeatVariable is not null)
            custom["repetitiveVar"] = new JObject { ["name"] = repeatVariable };

        return new JObject { ["custom"] = custom };
    }

    private void ConvertVariables(JObject grafana, JObject customDashboard, ISet<string> discoveredMetrics)
    {
        var grafanaVariables = grafana["templating"]?["list"] as JArray ?? new JArray();
        var variableConverter = new VariableConverter(_logger);
        customDashboard["variablesV2"] = variableConverter.ConvertVariables(
            grafanaVariables,
            discoveredMetrics,
            onDropped: AddDashboardDiagnostic);
    }

    private void AddDashboardDiagnostic(DashboardConversionDiagnostic diagnostic) =>
        _dashboardDiagnostics.Add(diagnostic);

    private static void ApplyTimeFrame(JObject grafana, JObject customDashboard)
    {
        var from = grafana["time"]?["from"]?.ToString();
        customDashboard["relativeTimeFrame"] = QueryHelpers.MapTimeFrame(from);
    }
}
