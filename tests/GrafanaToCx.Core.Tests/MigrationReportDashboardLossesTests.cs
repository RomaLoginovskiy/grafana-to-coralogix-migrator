using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Migration;

namespace GrafanaToCx.Core.Tests;

public class MigrationReportDashboardLossesTests
{
    private static MigrationReportEntry Entry(
        string dashboard,
        params DashboardConversionDiagnostic[] diagnostics) => new()
    {
        FolderTitle = "Ops",
        DashboardTitle = dashboard,
        Status = CheckpointStatus.Completed,
        CxDashboardId = "cx-123",
        DashboardDiagnostics = diagnostics
    };

    private static DashboardConversionDiagnostic Transformation(string id, string panel) =>
        new("transformation", id, "Transformation is not applied.",
            DashboardDiagnosticCodes.Transformation, panel);

    [Fact]
    public void CleanRun_OmitsTheSectionEntirely()
    {
        var report = new MigrationReport();
        report.Add(Entry("Board"));

        Assert.DoesNotContain("Elements not migrated", report.Build());
    }

    [Fact]
    public void Section_SummarisesCountsByKind()
    {
        var report = new MigrationReport();
        report.Add(Entry("Board",
            Transformation("merge", "Requests"),
            Transformation("merge", "Durations"),
            Transformation("organize", "Requests"),
            new DashboardConversionDiagnostic("annotation", "Pagerduty alerts",
                "Annotation queries are not emitted.", DashboardDiagnosticCodes.Annotation)));

        var text = report.Build();

        Assert.Contains("Elements not migrated", text);
        Assert.Contains("4 element(s) across 1 dashboard(s)", text);
        Assert.Contains("transformation (3)", text);
        Assert.Contains("- merge x2", text);
        Assert.Contains("- organize x1", text);
        Assert.Contains("annotation (1)", text);
    }

    [Fact]
    public void PerDashboardDetail_NamesTheOwningPanel()
    {
        var report = new MigrationReport();
        report.Add(Entry("Service Overview", Transformation("calculateField", "Error rate")));

        var text = report.Build();

        Assert.Contains("[LOST] Ops / Service Overview", text);
        Assert.Contains("transformation 'calculateField' [panel: Error rate]", text);
    }

    [Fact]
    public void DashboardWideElement_OmitsThePanelSuffix()
    {
        var report = new MigrationReport();
        report.Add(Entry("Board", new DashboardConversionDiagnostic(
            "dashboardLink", "Query Analytics", "Dashboard links are not emitted.",
            DashboardDiagnosticCodes.DashboardLink)));

        var text = report.Build();

        Assert.Contains("dashboardLink 'Query Analytics': Dashboard links are not emitted.", text);
        Assert.DoesNotContain("[panel:", text);
    }

    [Fact]
    public void OnlyAffectedDashboardsAreListed()
    {
        var report = new MigrationReport();
        report.Add(Entry("Clean Board"));
        report.Add(Entry("Lossy Board", Transformation("merge", "Requests")));

        var text = report.Build();

        Assert.Contains("1 element(s) across 1 dashboard(s)", text);
        Assert.Contains("[LOST] Ops / Lossy Board", text);
        Assert.DoesNotContain("[LOST] Ops / Clean Board", text);
    }

    [Fact]
    public void ExistingPanelDiagnosticsSectionIsUnaffected()
    {
        var report = new MigrationReport();
        report.Add(new MigrationReportEntry
        {
            FolderTitle = "Ops",
            DashboardTitle = "Board",
            Status = CheckpointStatus.Completed,
            CxDashboardId = "cx-1",
            ConversionDiagnostics =
            [
                new PanelConversionDiagnostic("Requests", "grafana-piechart-panel", "skipped",
                    "Unsupported Grafana panel type.", "UNS-PNL-001")
            ],
            DashboardDiagnostics = [Transformation("merge", "Requests")]
        });

        var text = report.Build();

        Assert.Contains("[WARN] Ops / Board", text);
        Assert.Contains("grafana-piechart-panel (Requests): skipped", text);
        Assert.Contains("[LOST] Ops / Board", text);
    }
}
