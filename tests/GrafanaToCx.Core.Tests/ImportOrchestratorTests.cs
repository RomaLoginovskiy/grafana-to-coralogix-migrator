using System.Net;
using GrafanaToCx.Core.ApiClient;
using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Migration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

public sealed class ImportOrchestratorTests
{
    [Fact]
    public async Task RunAsync_NewDashboard_UploadsAndMarksCompleted()
    {
        await using var h = TestHarness.Create();
        var plan = h.PlanFor(("A.json", "uid-a", "Alpha", "folder-1"));

        var summary = await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.Equal(1, summary.Completed);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(1, h.CxClient.UploadCallCount);
        Assert.Equal(0, h.CxClient.ReplaceCallCount);

        var entry = h.Checkpoint.Get("folder-1::uid:uid-a");
        Assert.NotNull(entry);
        Assert.Equal(CheckpointStatus.Completed, entry!.Status);
        Assert.Equal("cx-upload-1", entry.CxDashboardId);
        Assert.Equal("folder-1", entry.CxFolderId);
        Assert.Equal("A.json", entry.SourcePath);
    }

    [Fact]
    public async Task RunAsync_ExistingNameInSameFolder_ReplacesInsteadOfCreating()
    {
        await using var h = TestHarness.Create();
        h.CxClient.CatalogItems.Add(new DashboardCatalogItem("cx-existing", "Alpha", "folder-1"));

        var plan = h.PlanFor(("A.json", "uid-a", "Alpha", "folder-1"));
        await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.Equal(1, h.CxClient.ReplaceCallCount);
        Assert.Equal(0, h.CxClient.UploadCallCount);
        Assert.Equal("cx-existing", h.CxClient.LastReplaceDashboardId);
    }

    [Fact]
    public async Task RunAsync_ExistingNameInDifferentFolder_CreatesNew()
    {
        await using var h = TestHarness.Create();
        h.CxClient.CatalogItems.Add(new DashboardCatalogItem("cx-existing", "Alpha", "other-folder"));

        var plan = h.PlanFor(("A.json", "uid-a", "Alpha", "folder-1"));
        await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.Equal(0, h.CxClient.ReplaceCallCount);
        Assert.Equal(1, h.CxClient.UploadCallCount);
    }

    /// <summary>Regression for the N+1 fetch that ran once per file in the old import loop.</summary>
    [Fact]
    public async Task RunAsync_FetchesCatalogExactlyOnceForManyFiles()
    {
        await using var h = TestHarness.Create();
        var plan = h.PlanFor(
            ("A.json", "uid-a", "Alpha", "folder-1"),
            ("B.json", "uid-b", "Bravo", "folder-1"),
            ("C.json", "uid-c", "Charlie", "folder-1"),
            ("D.json", "uid-d", "Delta", "folder-1"),
            ("E.json", "uid-e", "Echo", "folder-1"));

        await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.Equal(1, h.CxClient.GetCatalogItemsCallCount);
        Assert.Equal(5, h.CxClient.UploadCallCount);
    }

    /// <summary>
    /// A static snapshot would make both files miss and both create, leaving two dashboards with the
    /// same name in one folder.
    /// </summary>
    [Fact]
    public async Task RunAsync_TwoFilesSameDashboardName_SecondReplacesFirstInsteadOfDuplicating()
    {
        await using var h = TestHarness.Create();
        var plan = h.PlanFor(
            ("A.json", "uid-a", "Same Name", "folder-1"),
            ("B.json", "uid-b", "Same Name", "folder-1"));

        await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.Equal(1, h.CxClient.UploadCallCount);
        Assert.Equal(1, h.CxClient.ReplaceCallCount);
        Assert.Equal("cx-upload-1", h.CxClient.LastReplaceDashboardId);
    }

    [Fact]
    public async Task RunAsync_OverwriteOff_SkipsCompletedCheckpointEntries()
    {
        await using var h = TestHarness.Create(overwriteExisting: false);
        h.Checkpoint.Upsert("folder-1::uid:uid-a", new CheckpointEntry
        {
            GrafanaUid = "uid-a",
            GrafanaTitle = "Alpha",
            Status = CheckpointStatus.Completed,
            CxDashboardId = "cx-old"
        });
        await h.Checkpoint.SaveAsync();

        var plan = h.PlanFor(("A.json", "uid-a", "Alpha", "folder-1"));
        var summary = await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Completed);
        Assert.Equal(0, h.CxClient.UploadCallCount);
        Assert.Equal(0, h.CxClient.ReplaceCallCount);
    }

    [Fact]
    public async Task RunAsync_OverwriteOn_ReprocessesCompletedCheckpointEntries()
    {
        await using var h = TestHarness.Create(overwriteExisting: true);
        h.Checkpoint.Upsert("folder-1::uid:uid-a", new CheckpointEntry
        {
            GrafanaUid = "uid-a",
            GrafanaTitle = "Alpha",
            Status = CheckpointStatus.Completed,
            CxDashboardId = "cx-old"
        });
        await h.Checkpoint.SaveAsync();

        var plan = h.PlanFor(("A.json", "uid-a", "Alpha", "folder-1"));
        var summary = await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, h.CxClient.ReplaceCallCount);
        Assert.Equal("cx-old", h.CxClient.LastReplaceDashboardId);
    }

    [Fact]
    public async Task RunAsync_StaleCxDashboardId_ReplaceFailsThenFallbackUploadSucceeds()
    {
        await using var h = TestHarness.Create(overwriteExisting: true);
        h.Checkpoint.Upsert("folder-1::uid:uid-a", new CheckpointEntry
        {
            GrafanaUid = "uid-a",
            GrafanaTitle = "Alpha",
            Status = CheckpointStatus.Completed,
            CxDashboardId = "cx-stale"
        });
        await h.Checkpoint.SaveAsync();
        h.CxClient.ReplaceResult = false;
        h.CxClient.UploadResult = DashboardUploadResult.Succeeded("cx-new");

        var plan = h.PlanFor(("A.json", "uid-a", "Alpha", "folder-1"));
        await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.Equal(1, h.CxClient.ReplaceCallCount);
        Assert.Equal(1, h.CxClient.UploadCallCount);

        var entry = h.Checkpoint.Get("folder-1::uid:uid-a");
        Assert.Equal(CheckpointStatus.Completed, entry!.Status);
        Assert.Equal("cx-new", entry.CxDashboardId);
    }

    [Fact]
    public async Task RunAsync_RetryableFailure_SetsFailedRetryableAndNextRetryAt()
    {
        await using var h = TestHarness.Create();
        h.CxClient.UploadResult = DashboardUploadResult.Failed(HttpStatusCode.ServiceUnavailable, "503");

        var plan = h.PlanFor(("A.json", "uid-a", "Alpha", "folder-1"));
        var summary = await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.Equal(1, summary.Failed);
        var entry = h.Checkpoint.Get("folder-1::uid:uid-a")!;
        Assert.Equal(CheckpointStatus.FailedRetryable, entry.Status);
        Assert.Equal(1, entry.RetryCount);
        Assert.NotNull(entry.NextRetryAt);
    }

    /// <summary>
    /// Retries are scheduled across process runs rather than looped in-process, so without this promotion
    /// a permanently-broken dashboard is reattempted on every future run forever.
    /// </summary>
    [Fact]
    public async Task RunAsync_RetryableFailureAtMaxRetries_IsPromotedToCriticalAndStopsRetrying()
    {
        await using var h = TestHarness.Create(maxRetries: 3);
        h.CxClient.UploadResult = DashboardUploadResult.Failed(HttpStatusCode.ServiceUnavailable, "503");

        var plan = h.PlanFor(("A.json", "uid-a", "Alpha", "folder-1"));
        h.Checkpoint.Upsert("folder-1::uid:uid-a", new CheckpointEntry
        {
            GrafanaUid = "uid-a",
            Status = CheckpointStatus.FailedRetryable,
            RetryCount = 2,
            NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await h.Checkpoint.SaveAsync();

        await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        var entry = h.Checkpoint.Get("folder-1::uid:uid-a")!;
        Assert.Equal(CheckpointStatus.FailedCritical, entry.Status);
        Assert.Equal(3, entry.RetryCount);
        Assert.Null(entry.NextRetryAt);
        Assert.Contains("gave up after 3", entry.ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_CriticalFailure_SetsFailedCriticalWithoutNextRetry()
    {
        await using var h = TestHarness.Create();
        h.CxClient.UploadResult = DashboardUploadResult.Failed(HttpStatusCode.BadRequest, "400 bad payload");

        var plan = h.PlanFor(("A.json", "uid-a", "Alpha", "folder-1"));
        await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        var entry = h.Checkpoint.Get("folder-1::uid:uid-a")!;
        Assert.Equal(CheckpointStatus.FailedCritical, entry.Status);
        Assert.Null(entry.NextRetryAt);
        Assert.Equal(0, entry.RetryCount);
    }

    [Fact]
    public async Task RunAsync_EntryWithFutureNextRetryAt_IsSkipped()
    {
        await using var h = TestHarness.Create();
        h.Checkpoint.Upsert("folder-1::uid:uid-a", new CheckpointEntry
        {
            GrafanaUid = "uid-a",
            GrafanaTitle = "Alpha",
            Status = CheckpointStatus.FailedRetryable,
            NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(30)
        });
        await h.Checkpoint.SaveAsync();

        var plan = h.PlanFor(("A.json", "uid-a", "Alpha", "folder-1"));
        var summary = await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, h.CxClient.UploadCallCount);
    }

    [Fact]
    public async Task RunAsync_ConversionThrows_MarksFailedCriticalAndContinuesRemainingFiles()
    {
        await using var h = TestHarness.Create();
        h.Converter.ThrowForNames.Add("Alpha");

        var plan = h.PlanFor(
            ("A.json", "uid-a", "Alpha", "folder-1"),
            ("B.json", "uid-b", "Bravo", "folder-1"));

        var summary = await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Completed);
        Assert.Equal(CheckpointStatus.FailedCritical, h.Checkpoint.Get("folder-1::uid:uid-a")!.Status);
        Assert.Equal(CheckpointStatus.Completed, h.Checkpoint.Get("folder-1::uid:uid-b")!.Status);
    }

    [Fact]
    public async Task RunAsync_ValidationFails_MarksFailedCriticalWithoutUploading()
    {
        await using var h = TestHarness.Create();
        h.Converter.EmitInvalidPayloadForNames.Add("Alpha");

        var plan = h.PlanFor(("A.json", "uid-a", "Alpha", "folder-1"));
        await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.Equal(0, h.CxClient.UploadCallCount);
        Assert.Equal(CheckpointStatus.FailedCritical, h.Checkpoint.Get("folder-1::uid:uid-a")!.Status);
    }

    [Fact]
    public async Task RunAsync_UnreadableSourceFile_MarksFailedCriticalAndContinues()
    {
        await using var h = TestHarness.Create();
        h.Reader.ThrowForPaths.Add("A.json");

        var plan = h.PlanFor(
            ("A.json", "uid-a", "Alpha", "folder-1"),
            ("B.json", "uid-b", "Bravo", "folder-1"));

        var summary = await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Completed);
    }

    [Fact]
    public async Task RunAsync_WritesCheckpointAfterEachFile()
    {
        await using var h = TestHarness.Create();
        var plan = h.PlanFor(
            ("A.json", "uid-a", "Alpha", "folder-1"),
            ("B.json", "uid-b", "Bravo", "folder-1"));

        await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        var reloaded = new CheckpointStore(h.ImportCheckpointFile);
        await reloaded.LoadAsync();
        Assert.Equal(2, reloaded.All.Count);
    }

    [Fact]
    public async Task RunAsync_WritesReportToConfiguredPath()
    {
        await using var h = TestHarness.Create();
        var plan = h.PlanFor(("A.json", "uid-a", "Alpha", "folder-1"));

        await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.True(File.Exists(h.ImportReportFile));
        var report = await File.ReadAllTextAsync(h.ImportReportFile);
        Assert.Contains("Alpha", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each entry must hold its own diagnostics snapshot — the converter reuses and clears one list, so
    /// storing the reference makes every report row show the last dashboard's diagnostics.
    /// </summary>
    [Fact]
    public async Task RunAsync_ReportKeepsPerDashboardDiagnostics()
    {
        await using var h = TestHarness.Create();
        h.Converter.DiagnosticsByName["Alpha"] = [new PanelConversionDiagnostic("P-alpha", "graph", "Degraded", "alpha reason")];
        h.Converter.DiagnosticsByName["Bravo"] = [new PanelConversionDiagnostic("P-bravo", "graph", "Degraded", "bravo reason")];

        var plan = h.PlanFor(
            ("A.json", "uid-a", "Alpha", "folder-1"),
            ("B.json", "uid-b", "Bravo", "folder-1"));

        await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        var report = await File.ReadAllTextAsync(h.ImportReportFile);
        Assert.Contains("alpha reason", report, StringComparison.Ordinal);
        Assert.Contains("bravo reason", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CheckpointPathEqualsMigrationCheckpoint_ThrowsBeforeAnyWork()
    {
        await using var h = TestHarness.Create();
        var sut = h.CreateSut(checkpointFileOverride: h.MigrationCheckpointFile);
        var plan = h.PlanFor(("A.json", "uid-a", "Alpha", "folder-1"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.RunAsync(plan, h.MigrationCheckpointFile));

        Assert.Equal(0, h.CxClient.UploadCallCount);
        Assert.Equal(0, h.CxClient.GetCatalogItemsCallCount);
    }

    [Fact]
    public void GuardCheckpointPaths_ThreeDistinctPaths_DoesNotThrow() =>
        ImportOrchestrator.GuardCheckpointPaths(
            "migration-checkpoint.json", "import-checkpoint.json", "grafana-import-checkpoint.json");

    [Theory]
    [InlineData("migration-checkpoint.json", "import-checkpoint.json", "import-checkpoint.json")]
    [InlineData("migration-checkpoint.json", "import-checkpoint.json", "migration-checkpoint.json")]
    [InlineData("migration-checkpoint.json", "import-checkpoint.json", "./IMPORT-checkpoint.json")]
    public void GuardCheckpointPaths_AnyTwoResolveToTheSameFile_Throws(
        string migrate, string import, string grafanaImport)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ImportOrchestrator.GuardCheckpointPaths(migrate, import, grafanaImport));

        Assert.Contains("separate checkpoint", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>An unconfigured flow has no file, so it cannot collide with one.</summary>
    [Fact]
    public void GuardCheckpointPaths_IgnoresNullAndWhitespace() =>
        ImportOrchestrator.GuardCheckpointPaths("import-checkpoint.json", null, "  ", "");

    [Fact]
    public async Task RunAsync_DoesNotModifyMigrationCheckpointFile()
    {
        await using var h = TestHarness.Create();
        const string migrateJson = """
        {
          "existing-uid": {
            "GrafanaUid": "existing-uid",
            "GrafanaTitle": "Migrated Dashboard",
            "FolderTitle": "General",
            "Status": "Completed",
            "CxDashboardId": "cx-migrated",
            "RetryCount": 0,
            "LastAttemptAt": "2026-03-11T10:32:00+00:00"
          }
        }
        """;
        await File.WriteAllTextAsync(h.MigrationCheckpointFile, migrateJson);

        var plan = h.PlanFor(("A.json", "uid-a", "Alpha", "folder-1"));
        await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.Equal(migrateJson, await File.ReadAllTextAsync(h.MigrationCheckpointFile));
    }

    [Fact]
    public async Task RunAsync_NoFolder_UsesNoneSentinelInCheckpointKey()
    {
        await using var h = TestHarness.Create();
        var plan = h.PlanFor(("A.json", "uid-a", "Alpha", null));

        await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.NotNull(h.Checkpoint.Get("(none)::uid:uid-a"));
    }

    [Fact]
    public async Task RunAsync_DashboardNameOverride_IsPassedToConverter()
    {
        await using var h = TestHarness.Create();
        var plan = new ImportPlan("/root",
        [
            new ImportPlanItem("/root/A.json", "A.json", "uid-a", "Lobby Platforms Dashboard V2",
                "folder-1", "WSP - webShop Platforms", DashboardNameOverride: "Primary")
        ]);

        await h.CreateSut().RunAsync(plan, h.MigrationCheckpointFile);

        Assert.Equal("Primary", h.Converter.LastOptions?.DashboardName);
        Assert.Equal("folder-1", h.Converter.LastOptions?.FolderId);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly string _dir;

        private TestHarness(string dir, ImportSettings settings)
        {
            _dir = dir;
            Settings = settings;
            Checkpoint = new CheckpointStore(settings.CheckpointFile);
        }

        public ImportSettings Settings { get; }
        public CheckpointStore Checkpoint { get; }
        public FakeConverter Converter { get; } = new();
        public FakeCxClient CxClient { get; } = new();
        public FakeSourceReader Reader { get; } = new();
        public MigrationReport Report { get; } = new();

        public string ImportCheckpointFile => Settings.CheckpointFile;
        public string ImportReportFile => Settings.ReportFile;
        public string MigrationCheckpointFile => Path.Combine(_dir, "migration-checkpoint.json");

        public static TestHarness Create(bool overwriteExisting = true, int maxRetries = 5)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"cx-import-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);

            return new TestHarness(dir, new ImportSettings
            {
                CheckpointFile = Path.Combine(dir, "import-checkpoint.json"),
                ReportFile = Path.Combine(dir, "import-report.txt"),
                OverwriteExisting = overwriteExisting,
                MaxRetries = maxRetries,
                InitialRetryDelaySeconds = 1
            });
        }

        public ImportPlan PlanFor(params (string Path, string? Uid, string Title, string? FolderId)[] items)
        {
            foreach (var item in items)
                Reader.TitlesByFileName[item.Path] = item.Title;

            return new ImportPlan("/root", items
                .Select(i => new ImportPlanItem(
                    Path.Combine("/root", i.Path), i.Path, i.Uid, i.Title, i.FolderId, i.FolderId ?? "(none)"))
                .ToList());
        }

        public ImportOrchestrator CreateSut(string? checkpointFileOverride = null)
        {
            var settings = checkpointFileOverride is null
                ? Settings
                : new ImportSettings
                {
                    CheckpointFile = checkpointFileOverride,
                    ReportFile = Settings.ReportFile,
                    OverwriteExisting = Settings.OverwriteExisting,
                    MaxRetries = Settings.MaxRetries,
                    InitialRetryDelaySeconds = Settings.InitialRetryDelaySeconds
                };

            // Wrapped in the real adapters rather than faking the ports directly, so these tests keep
            // covering the Coralogix replace-then-create sequence and the validator call that moved
            // out of the orchestrator.
            return new ImportOrchestrator(
                new CoralogixTransformer(Converter, new DashboardValidator()),
                new CoralogixDashboardPublisher(CxClient, NullLogger<CoralogixDashboardPublisher>.Instance),
                Checkpoint, Report, settings,
                NullLogger<ImportOrchestrator>.Instance, Reader);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSourceReader : IImportSourceReader
    {
        public HashSet<string> ThrowForPaths { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> TitlesByFileName { get; } = new(StringComparer.Ordinal);

        public Task<string> ReadAsync(string absolutePath, CancellationToken ct = default)
        {
            var fileName = Path.GetFileName(absolutePath);
            if (ThrowForPaths.Contains(fileName))
                throw new IOException($"cannot read {fileName}");

            var title = TitlesByFileName.TryGetValue(fileName, out var t)
                ? t
                : Path.GetFileNameWithoutExtension(absolutePath);

            return Task.FromResult(new JObject { ["title"] = title }.ToString());
        }
    }

    private sealed class FakeConverter : IGrafanaToCxConverter
    {
        private List<PanelConversionDiagnostic> _diagnostics = [];

        public HashSet<string> ThrowForNames { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EmitInvalidPayloadForNames { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<PanelConversionDiagnostic>> DiagnosticsByName { get; } = new(StringComparer.Ordinal);
        public ConversionOptions? LastOptions { get; private set; }

        public IReadOnlyList<PanelConversionDiagnostic> ConversionDiagnostics => _diagnostics;
        public IReadOnlyList<DashboardConversionDiagnostic> DashboardDiagnostics => [];
        public IReadOnlyList<JObject> ConversionDecisionEvents => [];

        public string Convert(string grafanaJson, ConversionOptions? options = null) =>
            ConvertToJObject(grafanaJson, options).ToString();

        public JObject ConvertToJObject(string grafanaJson, ConversionOptions? options = null)
        {
            LastOptions = options;

            // The real converter names dashboards from the JSON title unless overridden.
            var name = options?.DashboardName;
            if (string.IsNullOrWhiteSpace(name))
                name = JObject.Parse(grafanaJson)["title"]?.ToString() ?? "Untitled";

            if (ThrowForNames.Contains(name))
                throw new InvalidOperationException($"conversion blew up for {name}");

            // Mirrors the real converter: the diagnostics list is replaced on every call.
            _diagnostics = DiagnosticsByName.TryGetValue(name, out var d) ? [.. d] : [];

            if (EmitInvalidPayloadForNames.Contains(name))
                return new JObject { ["name"] = name };   // missing layout -> validation failure

            return new JObject
            {
                ["name"] = name,
                ["layout"] = new JObject { ["sections"] = new JArray() }
            };
        }
    }

    private sealed class FakeCxClient : ICoralogixDashboardsClient
    {
        public List<DashboardCatalogItem> CatalogItems { get; } = [];
        public bool ReplaceResult { get; set; } = true;
        public DashboardUploadResult UploadResult { get; set; } = DashboardUploadResult.Succeeded("cx-upload-1");
        public int GetCatalogItemsCallCount { get; private set; }
        public int ReplaceCallCount { get; private set; }
        public int UploadCallCount { get; private set; }
        public string? LastReplaceDashboardId { get; private set; }

        public Task<List<DashboardCatalogItem>> GetCatalogItemsAsync(CancellationToken ct = default)
        {
            GetCatalogItemsCallCount++;
            return Task.FromResult(CatalogItems.ToList());
        }

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
