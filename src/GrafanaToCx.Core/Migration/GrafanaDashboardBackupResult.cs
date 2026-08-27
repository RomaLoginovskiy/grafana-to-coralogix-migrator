namespace GrafanaToCx.Core.Migration;

/// <summary>
/// Outcome of a Grafana-side dashboard backup: how many dashboards were discovered,
/// how many landed in the archive, and what could not be read.
/// </summary>
public sealed record GrafanaDashboardBackupResult(
    int TotalDashboards,
    int SavedDashboards,
    IReadOnlyList<string> FailedDashboards,
    IReadOnlyList<string> FailedFolders)
{
    public bool Success => FailedDashboards.Count == 0 && FailedFolders.Count == 0;
}
