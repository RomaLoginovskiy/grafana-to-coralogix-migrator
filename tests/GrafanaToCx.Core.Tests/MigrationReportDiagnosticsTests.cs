using GrafanaToCx.Core.ApiClient;
using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Migration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Guards the aliasing hazard between <see cref="IGrafanaToCxConverter.ConversionDiagnostics"/> — which
/// exposes the converter's live backing list, cleared on every conversion — and
/// <see cref="MigrationReport"/>, which is only rendered at the end of a run.
/// </summary>
public sealed class MigrationReportDiagnosticsTests
{
    /// <summary>
    /// The end-to-end guard. The earlier tests below assert that <see cref="MigrationReport"/> keeps
    /// whatever it was handed, which stays true even when the caller hands it an aliased list — that
    /// is exactly how a whole run once came out with all 639 entries showing the last dashboard's
    /// diagnostics. This one drives the real orchestrator instead, so the defensive copy has to live
    /// in production code for it to pass.
    /// </summary>
    [Fact]
    public async Task RunAsync_ConverterReusesItsDiagnosticList_EachDashboardKeepsItsOwn()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"report-aliasing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var reportPath = Path.Combine(tempDir, "report.txt");
            var settings = new MigrationSettings
            {
                Grafana = new GrafanaSettings { Folders = [] },
                Coralogix = new CoralogixSettings { FolderId = "cx-folder-1" },
                Migration = new MigrationRunSettings
                {
                    CheckpointFile = Path.Combine(tempDir, "checkpoint.json"),
                    ReportFile = reportPath,
                    BackupFile = string.Empty
                }
            };

            var checkpoint = new CheckpointStore(settings.Migration.CheckpointFile);
            await checkpoint.LoadAsync();

            var sut = new MigrationOrchestrator(
                new TwoDashboardGrafanaClient(),
                new ListReusingConverter(),
                new AlwaysUploadsCxClient(),
                new DashboardValidator(),
                checkpoint,
                new MigrationReport(),
                settings,
                NullLogger<MigrationOrchestrator>.Instance);

            await sut.RunAsync();

            var report = await File.ReadAllTextAsync(reportPath);

            Assert.Contains("alpha reason", report, StringComparison.Ordinal);
            Assert.Contains("bravo reason", report, StringComparison.Ordinal);
            Assert.Contains("alpha loss", report, StringComparison.Ordinal);
            Assert.Contains("bravo loss", report, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

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
            // Stands in for the defensive copy MigrationOrchestrator.BuildReportEntry and
            // ImportOrchestrator.AttemptImportAsync perform.
            ConversionDiagnostics = diagnostics.ToList()
        };

    private sealed class TwoDashboardGrafanaClient : IGrafanaClient
    {
        public Task<List<GrafanaFolder>> GetFoldersAsync(IReadOnlyList<string> folderFilter, CancellationToken ct = default) =>
            Task.FromResult(new List<GrafanaFolder> { new(1, "folder-uid-1", "Folder A") });

        public Task<List<GrafanaDashboardRef>> GetDashboardsInFolderAsync(int folderId, CancellationToken ct = default) =>
            Task.FromResult(new List<GrafanaDashboardRef>
            {
                new("uid-alpha", "Dashboard Alpha", "Folder A"),
                new("uid-bravo", "Dashboard Bravo", "Folder A")
            });

        public Task<JObject?> GetDashboardByUidAsync(string uid, CancellationToken ct = default) =>
            Task.FromResult<JObject?>(new JObject
            {
                ["dashboard"] = new JObject
                {
                    ["title"] = uid == "uid-alpha" ? "Dashboard Alpha" : "Dashboard Bravo",
                    ["uid"] = uid,
                    ["panels"] = new JArray()
                }
            });
    }

    /// <summary>
    /// Reproduces the real converter's contract: one backing list per kind, cleared and refilled on
    /// every conversion, exposed directly rather than copied.
    /// </summary>
    private sealed class ListReusingConverter : IGrafanaToCxConverter
    {
        private readonly List<PanelConversionDiagnostic> _panel = [];
        private readonly List<DashboardConversionDiagnostic> _dashboard = [];

        public string Convert(string grafanaJson, ConversionOptions? options = null) =>
            ConvertToJObject(grafanaJson, options).ToString();

        public JObject ConvertToJObject(string grafanaJson, ConversionOptions? options = null)
        {
            _panel.Clear();
            _dashboard.Clear();

            var uid = JObject.Parse(grafanaJson)["dashboard"]?["uid"]?.ToString();
            var word = uid == "uid-alpha" ? "alpha" : "bravo";

            _panel.Add(new PanelConversionDiagnostic("Panel", "graph", "Degraded", $"{word} reason"));
            _dashboard.Add(new DashboardConversionDiagnostic("variable", word, $"{word} loss", DashboardDiagnosticCodes.Variable));

            return new JObject
            {
                ["name"] = uid,
                ["layout"] = new JObject { ["sections"] = new JArray() }
            };
        }

        public IReadOnlyList<PanelConversionDiagnostic> ConversionDiagnostics => _panel;
        public IReadOnlyList<DashboardConversionDiagnostic> DashboardDiagnostics => _dashboard;
        public IReadOnlyList<JObject> ConversionDecisionEvents => [];
    }

    private sealed class AlwaysUploadsCxClient : ICoralogixDashboardsClient
    {
        private int _uploads;

        public Task<List<DashboardCatalogItem>> GetCatalogItemsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<DashboardCatalogItem>());

        public Task<DashboardUploadResult> UploadDashboardAsync(JObject dashboard, bool isLocked = false, string? folderId = null, CancellationToken ct = default) =>
            Task.FromResult(DashboardUploadResult.Succeeded($"cx-{Interlocked.Increment(ref _uploads)}"));

        public Task<bool> ReplaceDashboardAsync(JObject dashboard, bool isLocked = false, string? folderId = null, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<string?> CreateDashboardAsync(JObject dashboard, bool isLocked = false, string? folderId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GetCatalogAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardCatalogItem>> GetCatalogItemsByFolderAsync(string folderId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> AssignDashboardToFolderAsync(string dashboardId, string folderId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JObject?> GetDashboardByIdAsync(string dashboardId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> DeleteDashboardAsync(string dashboardId, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
