namespace GrafanaToCx.Core.Converter;

public sealed class ConversionOptions
{
    public string? FolderId { get; init; }
    public string? DashboardName { get; init; }
    public bool SkipUnsupportedPanels { get; init; } = true;

    /// <summary>
    /// When true, a stat panel carrying several queries is emitted as one widget per query
    /// instead of keeping the first and dropping the rest. Grafana already draws one tile per
    /// query on such a panel, so this preserves the data — at the cost of more widgets, which
    /// changes the layout of dashboards built around the idiom. On by default.
    /// </summary>
    /// <remarks>
    /// Defaulted on because the alternative loses data: a Coralogix gauge holds one query, so with
    /// fan-out off every query after the first is discarded, and on the <c>convert</c> and
    /// <c>push</c> paths there is no report to notice it in. Extra widgets are visible and
    /// reversible; a missing query is neither. Set false to keep the original layout.
    /// </remarks>
    public bool FanOutMultiQueryPanels { get; init; } = true;
}
