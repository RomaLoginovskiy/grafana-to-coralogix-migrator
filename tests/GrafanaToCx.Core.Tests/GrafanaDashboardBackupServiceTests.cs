using System.IO.Compression;
using System.Text.Json;
using GrafanaToCx.Core.ApiClient;
using GrafanaToCx.Core.Migration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

public class GrafanaDashboardBackupServiceTests
{
    [Fact]
    public async Task BackupAsync_SavesEveryDashboardUnderItsFolder()
    {
        var folders = new List<GrafanaFolder> { new(1, "uid-a", "Folder A") };
        var client = new FakeGrafanaClient
        {
            DashboardsByFolder = { [1] = [new GrafanaDashboardRef("dash-1", "My Dashboard", "Folder A")] },
            DashboardsByUid = { ["dash-1"] = new JObject { ["uid"] = "dash-1" } }
        };

        var path = TempZipPath("grafana-backup-ok");
        try
        {
            var result = await Run(client, folders, path);

            Assert.True(result.Success);
            Assert.Equal(1, result.TotalDashboards);
            Assert.Equal(1, result.SavedDashboards);

            using var zip = ZipFile.OpenRead(path);
            Assert.Contains(zip.Entries, e => e.FullName == "Folder A/My Dashboard_dash-1.json");
            Assert.DoesNotContain(zip.Entries, e => e.FullName == "_manifest.json");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BackupAsync_SanitisesSeparatorsInFolderAndDashboardTitles()
    {
        // Only '/' is asserted here: the rest of the sanitiser keys off
        // Path.GetInvalidFileNameChars(), which differs between Windows and Unix.
        var folders = new List<GrafanaFolder> { new(1, "uid-a", "Platform/Ops") };
        var client = new FakeGrafanaClient
        {
            DashboardsByFolder = { [1] = [new GrafanaDashboardRef("dash-1", "p99/p50 latency", "Platform/Ops")] },
            DashboardsByUid = { ["dash-1"] = new JObject { ["uid"] = "dash-1" } }
        };

        var path = TempZipPath("grafana-backup-sanitise");
        try
        {
            await Run(client, folders, path);

            using var zip = ZipFile.OpenRead(path);
            var entry = Assert.Single(zip.Entries);
            Assert.Equal("Platform_Ops/p99_p50 latency_dash-1.json", entry.FullName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BackupAsync_UnreadableDashboard_IsReportedAndManifested()
    {
        var folders = new List<GrafanaFolder> { new(1, "uid-a", "Folder A") };
        var client = new FakeGrafanaClient
        {
            DashboardsByFolder =
            {
                [1] =
                [
                    new GrafanaDashboardRef("dash-1", "Good", "Folder A"),
                    new GrafanaDashboardRef("dash-2", "Bad", "Folder A")
                ]
            },
            DashboardsByUid = { ["dash-1"] = new JObject { ["uid"] = "dash-1" } }
            // dash-2 is absent, so the client returns null for it.
        };

        var path = TempZipPath("grafana-backup-partial");
        try
        {
            var result = await Run(client, folders, path);

            Assert.False(result.Success);
            Assert.Equal(2, result.TotalDashboards);
            Assert.Equal(1, result.SavedDashboards);
            Assert.Equal(["dash-2"], result.FailedDashboards);
            Assert.Empty(result.FailedFolders);

            var manifest = await ReadManifest(path);
            Assert.Equal(2, manifest.Expected);
            Assert.Equal(1, manifest.Written);
            Assert.Equal(["dash-2"], manifest.FailedIds);
            Assert.NotNull(manifest.Note);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BackupAsync_FolderListingFailure_SkipsFolderAndKeepsGoing()
    {
        var folders = new List<GrafanaFolder>
        {
            new(1, "uid-a", "Broken"),
            new(2, "uid-b", "Folder B")
        };
        var client = new FakeGrafanaClient
        {
            FailingFolderIds = { 1 },
            DashboardsByFolder = { [2] = [new GrafanaDashboardRef("dash-1", "Good", "Folder B")] },
            DashboardsByUid = { ["dash-1"] = new JObject { ["uid"] = "dash-1" } }
        };

        var path = TempZipPath("grafana-backup-folder-fail");
        try
        {
            var result = await Run(client, folders, path);

            Assert.False(result.Success);
            Assert.Equal(["Broken"], result.FailedFolders);
            Assert.Empty(result.FailedDashboards);
            // The healthy folder still made it into the archive.
            Assert.Equal(1, result.SavedDashboards);
            Assert.Equal(1, result.TotalDashboards);

            using var zip = ZipFile.OpenRead(path);
            Assert.Contains(zip.Entries, e => e.FullName == "Folder B/Good_dash-1.json");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BackupAsync_NoDashboards_WritesReadableArchiveWithManifest()
    {
        var folders = new List<GrafanaFolder> { new(1, "uid-a", "Empty") };
        var client = new FakeGrafanaClient { DashboardsByFolder = { [1] = [] } };

        var path = TempZipPath("grafana-backup-empty");
        try
        {
            var result = await Run(client, folders, path);

            Assert.True(result.Success);
            Assert.Equal(0, result.TotalDashboards);
            Assert.Equal(0, result.SavedDashboards);

            Assert.True(new FileInfo(path).Length > 22, "ZIP must not be the minimal 22-byte empty archive.");
            var manifest = await ReadManifest(path);
            Assert.Equal(0, manifest.Expected);
            Assert.NotNull(manifest.Note);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BackupAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var folders = new List<GrafanaFolder> { new(1, "uid-a", "Folder A") };
        var cts = new CancellationTokenSource();
        var client = new FakeGrafanaClient
        {
            DashboardsByFolder =
            {
                [1] =
                [
                    new GrafanaDashboardRef("dash-1", "One", "Folder A"),
                    new GrafanaDashboardRef("dash-2", "Two", "Folder A")
                ]
            },
            DashboardsByUid =
            {
                ["dash-1"] = new JObject { ["uid"] = "dash-1" },
                ["dash-2"] = new JObject { ["uid"] = "dash-2" }
            },
            OnGetDashboard = uid =>
            {
                if (uid == "dash-1")
                    cts.Cancel();
            }
        };

        var path = TempZipPath("grafana-backup-cancel");
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => Run(client, folders, path, cts.Token));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static Task<GrafanaDashboardBackupResult> Run(
        IGrafanaClient client,
        IReadOnlyList<GrafanaFolder> folders,
        string path,
        CancellationToken ct = default)
    {
        var sut = new GrafanaDashboardBackupService(
            client,
            NullLogger<GrafanaDashboardBackupService>.Instance);
        return sut.BackupAsync(folders, path, ct);
    }

    private static string TempZipPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.zip");

    private static async Task<BackupManifestDto> ReadManifest(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("_manifest.json");
        Assert.NotNull(entry);

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var manifest = JsonSerializer.Deserialize<BackupManifestDto>(await reader.ReadToEndAsync());
        Assert.NotNull(manifest);
        return manifest;
    }

    private sealed class BackupManifestDto
    {
        public int Expected { get; set; }
        public int Written { get; set; }
        public List<string> FailedIds { get; set; } = [];
        public List<string> FailedFolders { get; set; } = [];
        public string? Note { get; set; }
    }

    private sealed class FakeGrafanaClient : IGrafanaClient
    {
        public Dictionary<int, List<GrafanaDashboardRef>> DashboardsByFolder { get; } = [];
        public Dictionary<string, JObject> DashboardsByUid { get; } = [];
        public HashSet<int> FailingFolderIds { get; } = [];
        public Action<string>? OnGetDashboard { get; init; }

        public Task<List<GrafanaFolder>> GetFoldersAsync(IReadOnlyList<string> folderFilter, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<List<GrafanaDashboardRef>> GetDashboardsInFolderAsync(int folderId, CancellationToken ct = default)
        {
            if (FailingFolderIds.Contains(folderId))
                throw new HttpRequestException($"folder {folderId} unavailable");

            return Task.FromResult(DashboardsByFolder.TryGetValue(folderId, out var list) ? list : []);
        }

        public Task<JObject?> GetDashboardByUidAsync(string uid, CancellationToken ct = default)
        {
            OnGetDashboard?.Invoke(uid);
            return Task.FromResult(DashboardsByUid.TryGetValue(uid, out var json) ? json : null);
        }
    }
}
