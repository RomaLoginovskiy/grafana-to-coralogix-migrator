using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.GrafanaToGrafana;

/// <param name="Dashboard">The dashboard body, ready to be wrapped in the save envelope.</param>
/// <param name="Uid">The uid the dashboard will be published under — preserved or derived.</param>
public sealed record GrafanaTransformResult(
    JObject Dashboard,
    string Uid,
    string Title,
    int SchemaVersion,
    IReadOnlyList<GrafanaTransformDiagnostic> Diagnostics);

public interface IGrafanaDashboardTransform
{
    GrafanaTransformResult Transform(JObject source, GrafanaTransformOptions options);
}

/// <summary>
/// Rewrites a Grafana dashboard export into a body safe to send to <c>POST /api/dashboards/db</c>.
/// </summary>
/// <remarks>
/// Pure: the source is never mutated and the instance holds no state between calls.
/// <para>
/// Republishing is a no-op because of exactly four things — a stable <c>uid</c>, <c>overwrite: true</c>
/// on the envelope, an absent <c>id</c>, and an absent <c>version</c>. The uid is what Grafana matches
/// on; <c>overwrite</c> waives its version check; a foreign numeric <c>id</c> would match an unrelated
/// dashboard on the destination; a foreign <c>version</c> would be written verbatim on create and then
/// collide. Change any one and re-running stops being idempotent.
/// </para>
/// </remarks>
public sealed class GrafanaDashboardTransform : IGrafanaDashboardTransform
{
    private static readonly Regex ValidUid = new(@"^[A-Za-z0-9\-_]{1,40}$", RegexOptions.Compiled);

    /// <summary>
    /// Matches <c>$var</c> and <c>${var}</c>. Also matches an unsubstituted <c>${DS_X}</c> input
    /// placeholder, which is what we want — an input we could not resolve must be left visible rather
    /// than pointed somewhere arbitrary.
    /// </summary>
    private static readonly Regex VariableReference = new(@"^\$\{?[A-Za-z0-9_]+\}?$", RegexOptions.Compiled);

    /// <summary>
    /// Keys that belong to the save envelope rather than the dashboard body. Stripped at the <b>root
    /// only</b>: <c>panels[].targets[].metrics[].meta</c> is Elasticsearch extended-stats configuration
    /// and must survive.
    /// </summary>
    private static readonly string[] EnvelopeLeakKeys =
        ["meta", "folderId", "folderUid", "folderTitle", "isFolder", "slug", "url"];

    /// <summary>
    /// Datasource references Grafana resolves itself. Several spellings of the same built-ins exist across
    /// schema versions, and all of them are valid on any destination.
    /// </summary>
    /// <remarks>
    /// <c>__expr__</c> is the server-side expression engine (math, reduce, resample), not a backend at all —
    /// remapping it would repoint an expression at a real datasource and break the panel.
    /// </remarks>
    private static readonly HashSet<string> BuiltInDatasourceUids =
        new(StringComparer.OrdinalIgnoreCase)
        { "grafana", "-- Grafana --", "-- Mixed --", "-- Dashboard --", "__expr__" };

    private static readonly HashSet<string> BuiltInDatasourceTypes =
        new(StringComparer.OrdinalIgnoreCase)
        { "grafana", "datasource", "mixed", "dashboard", "__expr__" };

    public GrafanaTransformResult Transform(JObject source, GrafanaTransformOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        // Unwrap the {meta, dashboard} envelope that GET /api/dashboards/uid/:uid returns, which is also
        // what this tool's own backup writes. Raw "Save JSON to file" exports have the body at the root.
        var body = source["dashboard"] as JObject ?? source;
        var dashboard = (JObject)body.DeepClone();

        var diagnostics = new List<GrafanaTransformDiagnostic>();
        var resolver = new DatasourceResolver(options);

        var schemaVersion = dashboard.Value<int?>("schemaVersion") ?? 0;
        StripEnvelopeLeaks(dashboard, diagnostics);
        StripForeignIdentity(dashboard, diagnostics);

        var uid = ResolveUid(dashboard, options, diagnostics);
        dashboard["uid"] = uid;

        if (!string.IsNullOrWhiteSpace(options.TitleOverride))
            dashboard["title"] = options.TitleOverride;

        var title = dashboard.Value<string>("title") ?? string.Empty;

        if (schemaVersion > 0 && schemaVersion < options.SchemaVersionFloor)
        {
            diagnostics.Add(GrafanaTransformDiagnostic.Warn(
                TransformCodes.SchemaVersionOld, "schemaVersion", "schemaVersion", "Preserved",
                $"schemaVersion {schemaVersion} is below the expected floor of {options.SchemaVersionFloor}; " +
                "the destination will migrate it on load, which may change panel options"));
        }

        ResolveInputs(dashboard, resolver, diagnostics);
        RewriteDatasources(dashboard, resolver, diagnostics);
        RewriteDatasourceVariables(dashboard, resolver, schemaVersion, diagnostics);
        VisitPanels(dashboard, options, diagnostics);
        CheckRequiredPlugins(dashboard, options, diagnostics);

        return new GrafanaTransformResult(dashboard, uid, title, schemaVersion, diagnostics);
    }

    // ── Identity ──────────────────────────────────────────────────────────────

    private static void StripEnvelopeLeaks(JObject dashboard, List<GrafanaTransformDiagnostic> diagnostics)
    {
        foreach (var key in EnvelopeLeakKeys)
        {
            if (dashboard.Remove(key))
            {
                diagnostics.Add(GrafanaTransformDiagnostic.Info(
                    TransformCodes.EnvelopeFieldStripped, key, key, "Removed",
                    "belongs to the save envelope, not the dashboard body"));
            }
        }
    }

    private static void StripForeignIdentity(JObject dashboard, List<GrafanaTransformDiagnostic> diagnostics)
    {
        var id = dashboard["id"];
        if (id is not null && id.Type != JTokenType.Null)
        {
            diagnostics.Add(GrafanaTransformDiagnostic.Info(
                TransformCodes.ForeignIdDropped, "id", "id", "Removed",
                $"numeric id {id} is the source instance's, and would match an unrelated dashboard on the destination",
                SourceValue: id.ToString()));
        }

        dashboard.Remove("id");

        // Removed rather than preserved: on create Grafana writes it verbatim, so a foreign version lands
        // on a brand-new dashboard and any later non-overwrite save fails the version check.
        dashboard.Remove("version");

        // UI dirty-tracking epoch. Keeping it makes identical content produce a different stored blob on
        // every republish, so no-op detection and version history both become noise.
        dashboard.Remove("iteration");
    }

    private static string ResolveUid(
        JObject dashboard, GrafanaTransformOptions options, List<GrafanaTransformDiagnostic> diagnostics)
    {
        var sourceUid = dashboard.Value<string>("uid");

        if (string.IsNullOrWhiteSpace(sourceUid))
        {
            var derived = DeriveUid(options.UidSeed);
            diagnostics.Add(GrafanaTransformDiagnostic.Info(
                TransformCodes.UidDerivedFromPath, "uid", "uid", "Derived",
                "source carries no uid; derived deterministically from the source path so re-runs are idempotent",
                TargetValue: derived));
            return derived;
        }

        if (options.ContestedSourceUids.Contains(sourceUid))
        {
            var derived = DeriveUid(options.UidSeed);
            diagnostics.Add(GrafanaTransformDiagnostic.Warn(
                TransformCodes.UidContested, "uid", "uid", "Derived",
                $"uid '{sourceUid}' is claimed by more than one source file in this run; every claimant " +
                "derives a uid instead, so the result does not depend on enumeration order",
                SourceValue: sourceUid, TargetValue: derived));
            return derived;
        }

        if (!ValidUid.IsMatch(sourceUid))
        {
            var derived = DeriveUid(options.UidSeed);
            diagnostics.Add(GrafanaTransformDiagnostic.Warn(
                TransformCodes.UidInvalid, "uid", "uid", "Derived",
                $"uid '{sourceUid}' is not accepted by Grafana (must match [A-Za-z0-9-_] and be at most 40 characters)",
                SourceValue: sourceUid, TargetValue: derived));
            return derived;
        }

        return sourceUid;
    }

    /// <summary>
    /// Seeded on the source path rather than the title: titles repeat across a real export set, so a
    /// title seed would make two unrelated dashboards publish over each other.
    /// </summary>
    private static string DeriveUid(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return "g2g-" + Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    // ── Datasources ───────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the <c>__inputs</c> declarations of a "share externally" export and substitutes their
    /// <c>${DS_X}</c> placeholders, then removes the block.
    /// </summary>
    /// <remarks>
    /// <c>/api/dashboards/db</c> does not consume <c>__inputs</c> — only <c>/api/dashboards/import</c>
    /// does — so leaving it in place would persist it and make the next export prompt again.
    /// Substitution is confined to datasource positions because <c>${DS_X}</c> also legitimately appears
    /// in panel titles and descriptions of shared exports.
    /// </remarks>
    private static void ResolveInputs(
        JObject dashboard, DatasourceResolver resolver, List<GrafanaTransformDiagnostic> diagnostics)
    {
        if (dashboard["__inputs"] is not JArray inputs)
        {
            dashboard.Remove("__inputs");
            return;
        }

        var substitutions = new Dictionary<string, TargetDatasource>(StringComparer.Ordinal);

        foreach (var input in inputs.OfType<JObject>())
        {
            if (!string.Equals(input.Value<string>("type"), "datasource", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = input.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var pluginId = input.Value<string>("pluginId");
            var label = input.Value<string>("label");
            var resolution = resolver.Resolve(null, label, pluginId);

            if (resolution.Datasource is not null)
            {
                substitutions["${" + name + "}"] = resolution.Datasource;
                diagnostics.Add(GrafanaTransformDiagnostic.Info(
                    TransformCodes.DatasourceRemapped, "__inputs", $"__inputs.{name}", "Resolved",
                    $"input placeholder bound to '{resolution.Datasource.Name}' by {Describe(resolution.Kind)}",
                    SourceValue: "${" + name + "}", TargetValue: resolution.Datasource.Uid));
            }
            else
            {
                diagnostics.Add(GrafanaTransformDiagnostic.Warn(
                    TransformCodes.InputUnresolved, "__inputs", $"__inputs.{name}", "Unresolved",
                    resolution.Detail ?? $"no destination datasource matches input '{name}' (plugin '{pluginId}')",
                    SourceValue: "${" + name + "}"));
            }
        }

        if (substitutions.Count > 0)
            SubstituteInputPlaceholders(dashboard, substitutions);

        dashboard.Remove("__inputs");
    }

    private static void SubstituteInputPlaceholders(
        JToken node, IReadOnlyDictionary<string, TargetDatasource> substitutions)
    {
        switch (node)
        {
            case JObject obj:
                foreach (var property in obj.Properties().ToList())
                {
                    if (IsDatasourceProperty(property.Name) &&
                        property.Value.Type == JTokenType.String &&
                        substitutions.TryGetValue(property.Value.Value<string>()!, out var ds))
                    {
                        property.Value = new JObject { ["type"] = ds.Type, ["uid"] = ds.Uid };
                        continue;
                    }

                    if (IsDatasourceProperty(property.Name) && property.Value is JObject dsObj &&
                        dsObj["uid"]?.Type == JTokenType.String &&
                        substitutions.TryGetValue(dsObj.Value<string>("uid")!, out var byUid))
                    {
                        dsObj["type"] = byUid.Type;
                        dsObj["uid"] = byUid.Uid;
                        continue;
                    }

                    SubstituteInputPlaceholders(property.Value, substitutions);
                }
                break;

            case JArray array:
                foreach (var child in array)
                    SubstituteInputPlaceholders(child, substitutions);
                break;
        }
    }

    /// <summary>
    /// Rewrites every <c>datasource</c> property in the document, wherever it appears.
    /// </summary>
    /// <remarks>
    /// Walks the whole tree rather than a fixed list of paths — references live under panels, nested row
    /// panels, targets, annotations and templating, and new panel plugins invent their own nesting.
    /// Only properties literally named <c>datasource</c> are touched, so nothing else can be caught by
    /// accident.
    /// </remarks>
    private void RewriteDatasources(
        JToken node, DatasourceResolver resolver, List<GrafanaTransformDiagnostic> diagnostics, string path = "")
    {
        switch (node)
        {
            case JObject obj:
                foreach (var property in obj.Properties().ToList())
                {
                    var childPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";

                    if (IsDatasourceProperty(property.Name))
                    {
                        RewriteDatasourceReference(property, resolver, diagnostics, childPath);
                        continue;
                    }

                    RewriteDatasources(property.Value, resolver, diagnostics, childPath);
                }
                break;

            case JArray array:
                for (var i = 0; i < array.Count; i++)
                    RewriteDatasources(array[i], resolver, diagnostics, $"{path}[{i}]");
                break;
        }
    }

    private void RewriteDatasourceReference(
        JProperty property, DatasourceResolver resolver, List<GrafanaTransformDiagnostic> diagnostics, string path)
    {
        switch (property.Value.Type)
        {
            // An explicit null means "inherit the panel or dashboard default" — a deliberate authoring
            // choice, and pinning it would change what the dashboard queries.
            case JTokenType.Null:
                return;

            case JTokenType.String:
            {
                var value = property.Value.Value<string>()!;

                // Variable references resolve at render time against a datasource picker; rewriting one
                // pins the picker forever.
                if (IsSelfResolvingDatasource(value))
                    return;

                var resolution = resolver.Resolve(null, value, null);
                if (resolution.Datasource is null)
                {
                    ReportUnresolved(diagnostics, path, resolution, value);
                    return;
                }

                // Upgraded from the pre-schemaVersion-33 bare-name form to the object form the
                // destination expects.
                property.Value = new JObject
                {
                    ["type"] = resolution.Datasource.Type,
                    ["uid"] = resolution.Datasource.Uid
                };

                diagnostics.Add(GrafanaTransformDiagnostic.Info(
                    TransformCodes.DatasourceRemapped, "datasource", path, "Remapped",
                    $"legacy name reference resolved to '{resolution.Datasource.Name}' by {Describe(resolution.Kind)}",
                    SourceValue: value, TargetValue: resolution.Datasource.Uid));
                return;
            }

            case JTokenType.Object:
            {
                var obj = (JObject)property.Value;
                var uid = obj.Value<string>("uid");
                var type = obj.Value<string>("type");

                if (IsSelfResolvingDatasource(uid, type)) return;

                var resolution = resolver.Resolve(uid, null, type);
                if (resolution.Datasource is null)
                {
                    ReportUnresolved(diagnostics, path, resolution, uid ?? type ?? "(empty)");
                    return;
                }

                if (string.Equals(uid, resolution.Datasource.Uid, StringComparison.Ordinal)) return;

                obj["uid"] = resolution.Datasource.Uid;
                obj["type"] = resolution.Datasource.Type;

                diagnostics.Add(GrafanaTransformDiagnostic.Info(
                    TransformCodes.DatasourceRemapped, "datasource", path, "Remapped",
                    $"resolved to '{resolution.Datasource.Name}' by {Describe(resolution.Kind)}",
                    SourceValue: uid, TargetValue: resolution.Datasource.Uid));
                return;
            }
        }
    }

    private static void ReportUnresolved(
        List<GrafanaTransformDiagnostic> diagnostics, string path, DatasourceResolution resolution, string sourceValue)
    {
        var code = resolution.Kind == DatasourceResolutionKind.Ambiguous
            ? TransformCodes.DatasourceAmbiguous
            : TransformCodes.DatasourceUnresolved;

        diagnostics.Add(GrafanaTransformDiagnostic.Warn(
            code, "datasource", path, "Unresolved",
            (resolution.Detail ?? "no destination datasource matched") +
            "; the reference is left unchanged so the panel reports the problem rather than querying the wrong backend",
            SourceValue: sourceValue));
    }

    /// <summary>
    /// Repoints the pinned selection of <c>datasource</c>-type template variables.
    /// </summary>
    /// <remarks>
    /// Only from schemaVersion 33, where <c>current.value</c> holds a uid — Grafana's own migrator will
    /// not convert a value that already looks like a uid, so a stale foreign one leaves the picker empty.
    /// Below 33 the value is a datasource <i>name</i>, which the destination's migrator resolves correctly
    /// against its own datasource list, so leaving it alone is both correct and less work.
    /// <para>
    /// <c>query</c> is never touched. On a datasource variable it is the plugin type filter, on a
    /// constant variable it is the literal value, and on a query variable it is often an object — rewriting
    /// it is how the prior art corrupted Prometheus regex constants into Lucene syntax.
    /// </para>
    /// </remarks>
    private static void RewriteDatasourceVariables(
        JObject dashboard, DatasourceResolver resolver, int schemaVersion, List<GrafanaTransformDiagnostic> diagnostics)
    {
        if (dashboard["templating"]?["list"] is not JArray list) return;

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not JObject variable) continue;
            if (!string.Equals(variable.Value<string>("type"), "datasource", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = $"templating.list[{i}]";
            var name = variable.Value<string>("name") ?? $"#{i}";

            if (schemaVersion < 33)
            {
                diagnostics.Add(GrafanaTransformDiagnostic.Info(
                    TransformCodes.DatasourceVariableLegacy, "templating", path, "Preserved",
                    $"variable '{name}' pins a datasource by name on schemaVersion {schemaVersion}; " +
                    "the destination's own migrator resolves it against its datasource list"));
                continue;
            }

            if (variable["current"] is not JObject current) continue;

            var currentUid = current.Value<string>("value");
            if (string.IsNullOrWhiteSpace(currentUid) || IsVariableReference(currentUid)) continue;

            var resolution = resolver.Resolve(currentUid, current.Value<string>("text"),
                variable.Value<string>("query"));

            if (resolution.Datasource is null)
            {
                diagnostics.Add(GrafanaTransformDiagnostic.Warn(
                    TransformCodes.DatasourceUnresolved, "templating", $"{path}.current", "Unresolved",
                    $"variable '{name}' pins datasource uid '{currentUid}', which does not exist on the destination; " +
                    "the picker will open empty until a user selects one",
                    SourceValue: currentUid));
                continue;
            }

            if (string.Equals(currentUid, resolution.Datasource.Uid, StringComparison.Ordinal)) continue;

            current["value"] = resolution.Datasource.Uid;
            current["text"] = resolution.Datasource.Name;

            // The stale option list would otherwise offer datasources that do not exist here.
            if (variable["options"] is JArray options) options.Clear();

            diagnostics.Add(GrafanaTransformDiagnostic.Info(
                TransformCodes.DatasourceRemapped, "templating", $"{path}.current", "Remapped",
                $"variable '{name}' repointed to '{resolution.Datasource.Name}' by {Describe(resolution.Kind)}",
                SourceValue: currentUid, TargetValue: resolution.Datasource.Uid));
        }
    }

    // ── Panels ────────────────────────────────────────────────────────────────

    private static void VisitPanels(
        JObject dashboard, GrafanaTransformOptions options, List<GrafanaTransformDiagnostic> diagnostics)
    {
        if (dashboard["panels"] is not JArray panels) return;
        VisitPanelArray(panels, options, diagnostics, "panels");
    }

    private static void VisitPanelArray(
        JArray panels, GrafanaTransformOptions options, List<GrafanaTransformDiagnostic> diagnostics, string path)
    {
        for (var i = 0; i < panels.Count; i++)
        {
            if (panels[i] is not JObject panel) continue;

            var panelPath = $"{path}[{i}]";
            var title = panel.Value<string>("title") ?? "(untitled)";

            if (options.DropLegacyPanelAlerts && panel["alert"] is not null)
            {
                panel.Remove("alert");
                diagnostics.Add(GrafanaTransformDiagnostic.Warn(
                    TransformCodes.LegacyAlertDropped, "alert", $"{panelPath}.alert", "Dropped",
                    $"panel '{title}' carries a pre-Grafana-9 alert rule, which the dashboard save API " +
                    "cannot recreate; recreate it as a unified alert on the destination"));
            }

            if (panel["libraryPanel"] is JObject library && options.TargetLibraryPanelUids.Count > 0)
            {
                var libraryUid = library.Value<string>("uid");
                if (libraryUid is not null && !options.TargetLibraryPanelUids.Contains(libraryUid))
                {
                    diagnostics.Add(GrafanaTransformDiagnostic.Warn(
                        TransformCodes.LibraryPanelMissing, "libraryPanel", $"{panelPath}.libraryPanel", "Unresolved",
                        $"library panel '{library.Value<string>("name") ?? libraryUid}' does not exist on the " +
                        "destination; the reference is kept because the export carries no panel body to inline",
                        SourceValue: libraryUid));
                }
            }

            // Row panels nest their children one level deep.
            if (panel["panels"] is JArray nested)
                VisitPanelArray(nested, options, diagnostics, $"{panelPath}.panels");
        }
    }

    /// <summary>
    /// Reports panel plugins the destination does not have, then drops the block.
    /// </summary>
    /// <remarks>
    /// <c>__requires</c> is declarative input for Grafana's import UI and is not consumed by the save API.
    /// A missing plugin still renders an error box on the panel, which is worth saying up front.
    /// </remarks>
    private static void CheckRequiredPlugins(
        JObject dashboard, GrafanaTransformOptions options, List<GrafanaTransformDiagnostic> diagnostics)
    {
        if (dashboard["__requires"] is JArray requires && options.TargetPluginIds.Count > 0)
        {
            foreach (var requirement in requires.OfType<JObject>())
            {
                if (!string.Equals(requirement.Value<string>("type"), "panel", StringComparison.OrdinalIgnoreCase))
                    continue;

                var pluginId = requirement.Value<string>("id");
                if (string.IsNullOrWhiteSpace(pluginId) || options.TargetPluginIds.Contains(pluginId))
                    continue;

                diagnostics.Add(GrafanaTransformDiagnostic.Warn(
                    TransformCodes.PluginMissing, "__requires", "__requires", "Unresolved",
                    $"panel plugin '{pluginId}' is not installed on the destination; panels using it will " +
                    "render an error box",
                    SourceValue: pluginId));
            }
        }

        dashboard.Remove("__requires");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsDatasourceProperty(string name) =>
        string.Equals(name, "datasource", StringComparison.Ordinal);

    /// <summary>
    /// True for references Grafana resolves itself — the built-in datasources, the expression engine, and
    /// anything pointing at a template variable. None of these should be remapped or reported.
    /// </summary>
    /// <remarks>
    /// Public so callers checking a transformed dashboard use the same list the transform did, rather than
    /// a second copy that can drift out of step with it.
    /// </remarks>
    public static bool IsSelfResolvingDatasource(string? uid, string? type = null) =>
        IsVariableReference(uid) ||
        (uid is not null && BuiltInDatasourceUids.Contains(uid)) ||
        (uid is null && type is not null && BuiltInDatasourceTypes.Contains(type)) ||
        (type is not null && string.Equals(type, "__expr__", StringComparison.OrdinalIgnoreCase));

    private static bool IsVariableReference(string? value) =>
        value is not null && value.StartsWith('$') && VariableReference.IsMatch(value);

    private static string Describe(DatasourceResolutionKind kind) => kind switch
    {
        DatasourceResolutionKind.Override => "an explicit override",
        DatasourceResolutionKind.ByUid => "uid",
        DatasourceResolutionKind.ByName => "name",
        DatasourceResolutionKind.ByType => "type",
        DatasourceResolutionKind.TargetDefault => "the destination default",
        _ => "no rule"
    };
}
