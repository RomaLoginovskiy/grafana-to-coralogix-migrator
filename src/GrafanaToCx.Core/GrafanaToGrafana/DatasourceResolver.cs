namespace GrafanaToCx.Core.GrafanaToGrafana;

/// <param name="Datasource">The destination datasource, when one was found.</param>
/// <param name="Kind">Why the resolver reached this answer — drives which diagnostic, if any, is emitted.</param>
public readonly record struct DatasourceResolution(TargetDatasource? Datasource, DatasourceResolutionKind Kind, string? Detail = null)
{
    public bool Resolved => Datasource is not null;
}

public enum DatasourceResolutionKind
{
    /// <summary>Matched an explicit entry in the configured override map.</summary>
    Override,

    /// <summary>Matched a destination datasource with the same name.</summary>
    ByName,

    /// <summary>Matched a destination datasource whose uid is already correct.</summary>
    ByUid,

    /// <summary>The only datasource of that type on the destination, or the default one among several.</summary>
    ByType,

    /// <summary>Fell back to the destination's default datasource.</summary>
    TargetDefault,

    /// <summary>The type exists more than once on the destination with no default to break the tie.</summary>
    Ambiguous,

    /// <summary>Nothing matched.</summary>
    Unresolved
}

/// <summary>
/// Maps a source datasource reference onto one that exists on the destination.
/// </summary>
/// <remarks>
/// Never coerces. When nothing matches, the caller leaves the reference byte-identical and reports it,
/// so a panel that cannot query says so instead of quietly querying the wrong backend — the failure mode
/// of forcing every unmatched reference to the default datasource.
/// </remarks>
public sealed class DatasourceResolver(GrafanaTransformOptions options)
{
    private readonly DatasourceIndex _index = options.Datasources;

    /// <param name="sourceUid">The source reference's uid, when it has one.</param>
    /// <param name="sourceName">The source reference's name — for legacy string refs this is the whole value.</param>
    /// <param name="sourceType">The source reference's plugin type, when it has one.</param>
    public DatasourceResolution Resolve(string? sourceUid, string? sourceName, string? sourceType)
    {
        // 1 & 2 — explicit configuration, by uid then by name.
        foreach (var key in new[] { sourceUid, sourceName })
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!options.DatasourceOverrides.TryGetValue(key, out var targetUid)) continue;

            var overridden = _index.ByUid(targetUid);
            if (overridden is not null)
                return new DatasourceResolution(overridden, DatasourceResolutionKind.Override);

            return new DatasourceResolution(null, DatasourceResolutionKind.Unresolved,
                $"override maps '{key}' to uid '{targetUid}', which does not exist on the target");
        }

        // 3 — the uid is already valid on the destination. Checked before name so an exact match wins.
        var byUid = _index.ByUid(sourceUid);
        if (byUid is not null)
            return new DatasourceResolution(byUid, DatasourceResolutionKind.ByUid);

        // 4 — same name. Load-bearing for legacy string refs, where the name is all we have.
        var byName = _index.ByName(sourceName);
        if (byName is not null)
            return new DatasourceResolution(byName, DatasourceResolutionKind.ByName);

        // 5 — sole datasource of that type, or the default among several.
        var byType = _index.SoleOrDefaultOfType(sourceType);
        if (byType is not null)
            return new DatasourceResolution(byType, DatasourceResolutionKind.ByType);

        if (_index.ByType(sourceType).Count > 1)
        {
            var candidates = string.Join(", ", _index.ByType(sourceType).Select(d => $"{d.Name} ({d.Uid})"));
            return new DatasourceResolution(null, DatasourceResolutionKind.Ambiguous,
                $"target has several '{sourceType}' datasources and none is the default: {candidates}");
        }

        // 6 — opt-in last resort.
        if (options.AllowTargetDefaultFallback && _index.Default is not null)
            return new DatasourceResolution(_index.Default, DatasourceResolutionKind.TargetDefault);

        return new DatasourceResolution(null, DatasourceResolutionKind.Unresolved,
            $"no override, uid, name, or type match for {Describe(sourceUid, sourceName, sourceType)}");
    }

    private static string Describe(string? uid, string? name, string? type)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(uid)) parts.Add($"uid '{uid}'");
        if (!string.IsNullOrWhiteSpace(name)) parts.Add($"name '{name}'");
        if (!string.IsNullOrWhiteSpace(type)) parts.Add($"type '{type}'");
        return parts.Count == 0 ? "an empty datasource reference" : string.Join(", ", parts);
    }
}
