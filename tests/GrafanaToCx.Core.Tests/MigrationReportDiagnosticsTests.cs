using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Migration;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Guards the aliasing hazard between <see cref="IGrafanaToCxConverter.ConversionDiagnostics"/> — which
/// exposes the converter's live backing list, cleared on every conversion — and
/// <see cref="MigrationReport"/>, which is only rendered at the end of a run.
/// </summary>
public sealed class MigrationReportDiagnosticsTests
{
    [Fact]
    public void Build_EntriesAddedFromAReusedList_KeepTheirOwnDiagnostics()
    {
        // Mimics a converter that refills one list per dashboard.
        var live = new List<PanelConversionDiagnostic>();
        var report = new MigrationReport();

        live.Clear();
        live.Add(new PanelConversionDiagnostic("Panel A", "graph", "Degraded", "alpha reason"));
        report.Add(BuildEntry("Dashboard A", live));

        live.Clear();
        live.Add(new PanelConversionDiagnostic("Panel B", "graph", "Degraded", "bravo reason"));
        report.Add(BuildEntry("Dashboard B", live));

        var built = report.Build();

        Assert.Contains("alpha reason", built, StringComparison.Ordinal);
        Assert.Contains("bravo reason", built, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ClearingTheSourceListAfterwards_DoesNotEmptyTheReport()
    {
        var live = new List<PanelConversionDiagnostic>
        {
            new("Panel A", "graph", "Degraded", "alpha reason")
        };

        var report = new MigrationReport();
        report.Add(BuildEntry("Dashboard A", live));

        live.Clear();

        Assert.Contains("alpha reason", report.Build(), StringComparison.Ordinal);
    }

    private static MigrationReportEntry BuildEntry(string title, IReadOnlyList<PanelConversionDiagnostic> diagnostics) =>
        new()
        {
            FolderTitle = "General",
            DashboardTitle = title,
            Status = CheckpointStatus.Completed,
            CxDashboardId = "cx-1",
            // Same defensive copy MigrationOrchestrator.BuildReportEntry and
            // ImportOrchestrator.AttemptImportAsync perform.
            ConversionDiagnostics = diagnostics.ToList()
        };
}
