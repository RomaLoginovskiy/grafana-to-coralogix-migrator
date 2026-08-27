using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.GrafanaToGrafana;

/// <summary>One datasource on the destination Grafana, as returned by <c>GET /api/datasources</c>.</summary>
public sealed record TargetDatasource(string Uid, string Name, string Type, bool IsDefault);

/// <summary>
/// Lookup structures over the destination's datasources, built once per run.
/// </summary>
/// <remarks>
/// Type lookups deliberately expose the ambiguous case instead of picking arbitrarily: when a source
/// dashboard names a Prometheus datasource that does not exist on the target and the target has two
/// Prometheus datasources, guessing silently sends the queries somewhere the author never chose.
/// </remarks>
public sealed class DatasourceIndex
{
    private readonly Dictionary<string, TargetDatasource> _byName;
    private readonly Dictionary<string, TargetDatasource> _byUid;
    private readonly Dictionary<string, List<TargetDatasource>> _byType;

    public DatasourceIndex(IEnumerable<TargetDatasource> datasources)
    {
        var all = datasources.ToList();

        All = all;
        Default = all.FirstOrDefault(d => d.IsDefault);

        _byName = new Dictionary<string, TargetDatasource>(StringComparer.OrdinalIgnoreCase);
        _byUid = new Dictionary<string, TargetDatasource>(StringComparer.Ordinal);
        _byType = new Dictionary<string, List<TargetDatasource>>(StringComparer.OrdinalIgnoreCase);

        foreach (var ds in all)
        {
            _byName.TryAdd(ds.Name, ds);
            _byUid.TryAdd(ds.Uid, ds);

            if (!_byType.TryGetValue(ds.Type, out var list))
                _byType[ds.Type] = list = [];
            list.Add(ds);
        }
    }

    public IReadOnlyList<TargetDatasource> All { get; }

    public TargetDatasource? Default { get; }

    public TargetDatasource? ByName(string? name) =>
        name is not null && _byName.TryGetValue(name, out var ds) ? ds : null;

    public TargetDatasource? ByUid(string? uid) =>
        uid is not null && _byUid.TryGetValue(uid, out var ds) ? ds : null;

    public IReadOnlyList<TargetDatasource> ByType(string? type) =>
        type is not null && _byType.TryGetValue(type, out var list) ? list : [];

    /// <summary>
    /// The single datasource of <paramref name="type"/>, or the default one when several share the type.
    /// Null when the type is absent, or present more than once with no default among them.
    /// </summary>
    public TargetDatasource? SoleOrDefaultOfType(string? type)
    {
        var candidates = ByType(type);
        return candidates.Count switch
        {
            0 => null,
            1 => candidates[0],
            _ => candidates.FirstOrDefault(d => d.IsDefault)
        };
    }

    /// <summary>Parses a <c>GET /api/datasources</c> response body.</summary>
    public static DatasourceIndex FromApiResponse(JArray datasources) =>
        new(datasources
            .OfType<JObject>()
            .Select(Read)
            .Where(d => !string.IsNullOrEmpty(d.Uid)));

    /// <summary>
    /// Parses the <c>datasources</c> map of a <c>GET /api/frontend/settings</c> response body — the route
    /// Grafana's own UI reads, and the only one a non-admin credential can use.
    /// </summary>
    /// <remarks>
    /// Entries are keyed by datasource name and carry the same uid/name/type/isDefault fields as
    /// <c>/api/datasources</c>, so the resulting index is interchangeable with
    /// <see cref="FromApiResponse"/> — with one exception this method removes. Unlike
    /// <c>/api/datasources</c>, this map also contains Grafana's built-in pseudo-datasources
    /// (<c>-- Grafana --</c>, <c>-- Mixed --</c>, <c>-- Dashboard --</c>). They are dropped: all three
    /// report type <c>datasource</c>, so keeping them would make every type lookup for that type
    /// ambiguous, and they are never a valid destination for a remapped panel query.
    /// </remarks>
    public static DatasourceIndex FromFrontendSettings(JObject settings) =>
        new((settings["datasources"] as JObject ?? [])
            .Properties()
            .Select(p => p.Value as JObject)
            .OfType<JObject>()
            .Select(Read)
            .Where(d => !string.IsNullOrEmpty(d.Uid) && !IsBuiltIn(d)));

    /// <summary>
    /// Grafana names and uids its built-ins with a leading <c>--</c>; the uid is checked too because the
    /// built-in Grafana datasource is uid <c>grafana</c> under the name <c>-- Grafana --</c>.
    /// </summary>
    private static bool IsBuiltIn(TargetDatasource ds) =>
        ds.Name.StartsWith("--", StringComparison.Ordinal)
        || ds.Uid.StartsWith("--", StringComparison.Ordinal)
        || string.Equals(ds.Uid, "grafana", StringComparison.Ordinal);

    private static TargetDatasource Read(JObject d) =>
        new(d.Value<string>("uid") ?? string.Empty,
            d.Value<string>("name") ?? string.Empty,
            d.Value<string>("type") ?? string.Empty,
            d.Value<bool?>("isDefault") ?? false);

    public static DatasourceIndex Empty { get; } = new([]);
}
