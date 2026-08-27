using System.Text;
using GrafanaToCx.Core.Converter;

namespace GrafanaToCx.Core.Migration;

public sealed class MigrationReportEntry
{
    public string FolderTitle { get; init; } = string.Empty;
    public string DashboardTitle { get; init; } = string.Empty;
    public CheckpointStatus Status { get; init; }
    public string? CxDashboardId { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<PanelConversionDiagnostic> ConversionDiagnostics { get; init; } = [];
    public IReadOnlyList<DashboardConversionDiagnostic> DashboardDiagnostics { get; init; } = [];
}

public sealed class MigrationReport
{
    private readonly List<MigrationReportEntry> _entries = [];

    public void Add(MigrationReportEntry entry) => _entries.Add(entry);

    public string Build()
    {
        var succeeded = _entries.Count(e => e.Status == CheckpointStatus.Completed);
        var critical = _entries.Count(e => e.Status == CheckpointStatus.FailedCritical);
        var retryable = _entries.Count(e => e.Status == CheckpointStatus.FailedRetryable);
        var skipped = _entries.Count(e => e.Status == CheckpointStatus.Pending);

        var sb = new StringBuilder();
        sb.AppendLine("Migration Report");
        sb.AppendLine("================");
        sb.AppendLine($"Total:                {_entries.Count}");
        sb.AppendLine($"Succeeded:            {succeeded}");
        sb.AppendLine($"Failed (critical):    {critical}");
        sb.AppendLine($"Failed (retryable):   {retryable}  <- checkpoint saved, re-run to retry");
        if (skipped > 0)
            sb.AppendLine($"Skipped (already done): {skipped}");
        sb.AppendLine();

        foreach (var e in _entries.Where(e => e.Status == CheckpointStatus.Completed))
            sb.AppendLine($"[OK] {e.FolderTitle} / {e.DashboardTitle}  ->  CX ID: {e.CxDashboardId}");

        foreach (var e in _entries.Where(e => e.Status == CheckpointStatus.FailedCritical))
            sb.AppendLine($"[FAIL] {e.FolderTitle} / {e.DashboardTitle}  ->  {e.ErrorMessage}");

        foreach (var e in _entries.Where(e => e.Status == CheckpointStatus.FailedRetryable))
            sb.AppendLine($"[RETRY] {e.FolderTitle} / {e.DashboardTitle}  ->  {e.ErrorMessage}");

        foreach (var e in _entries.Where(e => e.ConversionDiagnostics.Count > 0))
        {
            sb.AppendLine($"[WARN] {e.FolderTitle} / {e.DashboardTitle}");
            foreach (var diagnostic in e.ConversionDiagnostics)
            {
                sb.AppendLine($"  - {diagnostic.PanelType} ({diagnostic.PanelTitle}): {diagnostic.Outcome} - {diagnostic.Reason}");
            }
        }

        AppendDashboardLosses(sb);

        return sb.ToString();
    }

    /// <summary>
    /// Elements that are not panels — annotations, links, repeats, transformations, variables.
    /// Reported in their own section, grouped by kind, because a long flat list of these
    /// would bury the panel diagnostics above.
    /// </summary>
    private void AppendDashboardLosses(StringBuilder sb)
    {
        var affected = _entries.Where(e => e.DashboardDiagnostics.Count > 0).ToList();
        if (affected.Count == 0)
            return;

        var total = affected.Sum(e => e.DashboardDiagnostics.Count);

        sb.AppendLine();
        sb.AppendLine("Elements not migrated");
        sb.AppendLine("=====================");
        sb.AppendLine($"{total} element(s) across {affected.Count} dashboard(s) have no conversion path.");
        sb.AppendLine("These are not panels and do not appear above.");
        sb.AppendLine();

        foreach (var kind in affected
                     .SelectMany(e => e.DashboardDiagnostics)
                     .GroupBy(d => d.ElementKind)
                     .OrderByDescending(g => g.Count()))
        {
            sb.AppendLine($"{kind.Key} ({kind.Count()})");
            foreach (var byName in kind.GroupBy(d => d.ElementName).OrderByDescending(g => g.Count()))
                sb.AppendLine($"  - {byName.Key} x{byName.Count()}");
        }

        sb.AppendLine();

        foreach (var entry in affected)
        {
            sb.AppendLine($"[LOST] {entry.FolderTitle} / {entry.DashboardTitle}");
            foreach (var diagnostic in entry.DashboardDiagnostics)
            {
                var where = diagnostic.PanelTitle is { Length: > 0 } p ? $" [panel: {p}]" : string.Empty;
                sb.AppendLine($"  - {diagnostic.ElementKind} '{diagnostic.ElementName}'{where}: {diagnostic.Reason}");
            }
        }
    }

    public async Task SaveAsync(string filePath, CancellationToken ct = default)
    {
        await File.WriteAllTextAsync(filePath, Build(), ct);
    }
}
