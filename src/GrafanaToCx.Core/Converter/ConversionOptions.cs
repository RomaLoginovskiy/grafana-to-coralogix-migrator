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
    /// materially changes the layout of dashboards built around the idiom. Off by default.
    /// </summary>
    public bool FanOutMultiQueryPanels { get; init; }
}
