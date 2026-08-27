using GrafanaToCx.Core.ApiClient;
using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Migration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

public sealed class MigrationOrchestratorOverwriteTests
{
    [Fact]
    public async Task RunAsync_OverwriteOn_ReprocessesCompletedEntries()
    {
        await using var harness = await TestHarness.CreateAsync(overwriteExisting: true);
        harness.Checkpoint.Upsert(new CheckpointEntry
        {
            GrafanaUid = TestHarness.DashboardUid,
            GrafanaTitle = TestHarness.DashboardTitle,
            FolderTitle = TestHarness.FolderTitle,
            Status = CheckpointStatus.Completed,
            CxDashboardId = "cx-existing-1"
        });
        await harness.Checkpoint.SaveAsync();

        harness.CxClient.ReplaceResult = true;

        var sut = harness.CreateSut();
        await sut.RunAsync();

        Assert.Equal(1, harness.GrafanaClient.GetDashboardByUidCallCount);
        Assert.Equal(1, harness.CxClient.ReplaceCallCount);
        Assert.Equal(0, harness.CxClient.UploadCallCount);
    }

    [Fact]
    public async Task RunAsync_OverwriteOn_StaleCheckpointIdFallsBackToUploadAndSucceeds()
    {
        await using var harness = await TestHarness.CreateAsync(overwriteExisting: true);
        harness.Checkpoint.Upsert(new CheckpointEntry
        {
            GrafanaUid = TestHarness.DashboardUid,
            GrafanaTitle = TestHarness.DashboardTitle,
            FolderTitle = TestHarness.FolderTitle,
            Status = CheckpointStatus.Completed,
            CxDashboardId = "cx-stale-id"
        });
        await harness.Checkpoint.SaveAsync();

        harness.CxClient.ReplaceResult = false;
        harness.CxClient.UploadResult = DashboardUploadResult.Succeeded("cx-new-id");

        var sut = harness.CreateSut();
        await sut.RunAsync();

        var saved = harness.Checkpoint.Get(TestHarness.DashboardUid);
        Assert.NotNull(saved);
        Assert.Equal(CheckpointStatus.Completed, saved!.Status);
        Assert.Equal("cx-new-id", saved.CxDashboardId);
        Assert.Equal(1, harness.CxClient.ReplaceCallCount);
        Assert.Equal(1, harness.CxClient.UploadCallCount);
        Assert.Equal("cx-stale-id", harness.CxClient.LastReplaceDashboardId);
    }

    [Fact]
    public async Task RunAsync_OverwriteOn_NoMatchCreatesSuccessfully()
    {
        await using var harness = await TestHarness.CreateAsync(overwriteExisting: true);
        harness.CxClient.CatalogItems =
        [
            new DashboardCatalogItem("other-id", "Other Dashboard", TestHarness.FolderId)
        ];
        harness.CxClient.UploadResult = DashboardUploadResult.Succeeded("cx-created-id");

        var sut = harness.CreateSut();
        await sut.RunAsync();

        var saved = harness.Checkpoint.Get(TestHarness.DashboardUid);
        Assert.NotNull(saved);
        Assert.Equal(CheckpointStatus.Completed, saved!.Status);
        Assert.Equal("cx-created-id", saved.CxDashboardId);
        Assert.Equal(0, harness.CxClient.ReplaceCallCount);
        Assert.Equal(1, harness.CxClient.UploadCallCount);
    }

    [Fact]
    public async Task RunAsync_OverwriteOff_SkipsCompletedEntries()
    {
        await using var harness = await TestHarness.CreateAsync(overwriteExisting: false);
        harness.Checkpoint.Upsert(new CheckpointEntry
        {
            GrafanaUid = TestHarness.DashboardUid,
            GrafanaTitle = TestHarness.DashboardTitle,
            FolderTitle = TestHarness.FolderTitle,
            Status = CheckpointStatus.Completed,
            CxDashboardId = "cx-existing-1"
        });
        await harness.Checkpoint.SaveAsync();

        var sut = harness.CreateSut();
        await sut.RunAsync();

        Assert.Equal(0, harness.GrafanaClient.GetDashboardByUidCallCount);
        Assert.Equal(0, harness.Converter.ConvertToJObjectCallCount);
        Assert.Equal(0, harness.CxClient.ReplaceCallCount);
        Assert.Equal(0, harness.CxClient.UploadCallCount);
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly string _tempDir;
        private readonly CheckpointStore _checkpoint;

        private TestHarness(string tempDir, string checkpointPath)
        {
            _tempDir = tempDir;
            _checkpoint = new CheckpointStore(checkpointPath);
        }

        public const string FolderId = "cx-folder-1";
        public const string FolderTitle = "Folder A";
        public const string DashboardUid = "uid-1";
        public const string DashboardTitle = "Dashboard One";

        public FakeGrafanaClient GrafanaClient { get; } = new();
        public FakeConverter Converter { get; } = new();
        public FakeCoralogixDashboardsClient CxClient { get; } = new();
        public CheckpointStore Checkpoint => _checkpoint;

        public static async Task<TestHarness> CreateAsync(bool overwriteExisting)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"migration-overwrite-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            var checkpointPath = Path.Combine(tempDir, "checkpoint.json");
            var reportPath = Path.Combine(tempDir, "report.txt");
            var harness = new TestHarness(tempDir, checkpointPath)
            {
                Settings = new MigrationSettings
                {
                    Grafana = new GrafanaSettings
                    {
                        Folders = []
                    },
                    Coralogix = new CoralogixSettings
                    {
                        FolderId = FolderId,
                        OverwriteExisting = overwriteExisting
                    },
                    Migration = new MigrationRunSettings
                    {
                        CheckpointFile = checkpointPath,
                        ReportFile = reportPath,
                        BackupFile = string.Empty
                    }
                }
            };

            await harness.Checkpoint.LoadAsync();
            return harness;
        }

        public required MigrationSettings Settings { get; init; }

        public MigrationOrchestrator CreateSut()
        {
            return new MigrationOrchestrator(
                GrafanaClient,
                Converter,
                CxClient,
                new DashboardValidator(),
                Checkpoint,
                new MigrationReport(),
                Settings,
                NullLogger<MigrationOrchestrator>.Instance);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeGrafanaClient : IGrafanaClient
    {
        public int GetDashboardByUidCallCount { get; private set; }

        public Task<List<GrafanaFolder>> GetFoldersAsync(IReadOnlyList<string> folderFilter, CancellationToken ct = default) =>
            Task.FromResult(new List<GrafanaFolder> { new(1, "folder-uid-1", TestHarness.FolderTitle) });

        public Task<List<GrafanaDashboardRef>> GetDashboardsInFolderAsync(int folderId, CancellationToken ct = default) =>
            Task.FromResult(new List<GrafanaDashboardRef> { new(TestHarness.DashboardUid, TestHarness.DashboardTitle, TestHarness.FolderTitle) });

        public Task<JObject?> GetDashboardByUidAsync(string uid, CancellationToken ct = default)
        {
            GetDashboardByUidCallCount++;

            var payload = new JObject
            {
                ["dashboard"] = new JObject
                {
                    ["title"] = TestHarness.DashboardTitle,
                    ["panels"] = new JArray()
                }
            };

            return Task.FromResult<JObject?>(payload);
        }
    }

    private sealed class FakeConverter : IGrafanaToCxConverter
    {
        public int ConvertToJObjectCallCount { get; private set; }

        public string Convert(string grafanaJson, ConversionOptions? options = null) =>
            ConvertToJObject(grafanaJson, options).ToString();

        public JObject ConvertToJObject(string grafanaJson, ConversionOptions? options = null)
        {
            ConvertToJObjectCallCount++;
            return new JObject
            {
                ["name"] = TestHarness.DashboardTitle,
                ["layout"] = new JObject
                {
                    ["sections"] = new JArray()
                }
            };
        }

        public IReadOnlyList<PanelConversionDiagnostic> ConversionDiagnostics => [];
        public IReadOnlyList<DashboardConversionDiagnostic> DashboardDiagnostics => [];
        public IReadOnlyList<JObject> ConversionDecisionEvents => [];
    }

    private sealed class FakeCoralogixDashboardsClient : ICoralogixDashboardsClient
    {
        public List<DashboardCatalogItem> CatalogItems { get; set; } = [];
        public bool ReplaceResult { get; set; } = true;
        public DashboardUploadResult UploadResult { get; set; } = DashboardUploadResult.Succeeded("cx-upload-1");
        public int ReplaceCallCount { get; private set; }
        public int UploadCallCount { get; private set; }
        public string? LastReplaceDashboardId { get; private set; }

        public Task<List<DashboardCatalogItem>> GetCatalogItemsAsync(CancellationToken ct = default) =>
            Task.FromResult(CatalogItems.ToList());

        public Task<bool> ReplaceDashboardAsync(JObject dashboard, bool isLocked = false, string? folderId = null, CancellationToken ct = default)
        {
            ReplaceCallCount++;
            LastReplaceDashboardId = dashboard["id"]?.ToString();
            return Task.FromResult(ReplaceResult);
        }

        public Task<DashboardUploadResult> UploadDashboardAsync(JObject dashboard, bool isLocked = false, string? folderId = null, CancellationToken ct = default)
        {
            UploadCallCount++;
            return Task.FromResult(UploadResult);
        }

        public Task<string?> CreateDashboardAsync(JObject dashboard, bool isLocked = false, string? folderId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GetCatalogAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardCatalogItem>> GetCatalogItemsByFolderAsync(string folderId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> AssignDashboardToFolderAsync(string dashboardId, string folderId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JObject?> GetDashboardByIdAsync(string dashboardId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> DeleteDashboardAsync(string dashboardId, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
