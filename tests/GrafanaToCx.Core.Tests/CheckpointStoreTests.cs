using GrafanaToCx.Core.Migration;

namespace GrafanaToCx.Core.Tests;

public sealed class CheckpointStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cx-checkpoint-{Guid.NewGuid():N}");

    public CheckpointStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    /// <summary>
    /// The shape migrate has always written. Adding fields to <see cref="CheckpointEntry"/> must not
    /// break loading files produced before those fields existed.
    /// </summary>
    private const string LegacyCheckpointJson = """
    {
      "abc123": {
        "GrafanaUid": "abc123",
        "GrafanaTitle": "Existing Dashboard",
        "FolderTitle": "General",
        "Status": "Completed",
        "CxDashboardId": "cx-1",
        "ErrorMessage": null,
        "RetryCount": 0,
        "NextRetryAt": null,
        "LastAttemptAt": "2026-03-11T10:32:00+00:00"
      }
    }
    """;

    [Fact]
    public async Task LoadAsync_LegacyCheckpointWithoutNewFields_DeserializesWithNulls()
    {
        var file = PathFor("migration-checkpoint.json");
        await File.WriteAllTextAsync(file, LegacyCheckpointJson);

        var store = new CheckpointStore(file);
        await store.LoadAsync();

        var entry = store.Get("abc123");
        Assert.NotNull(entry);
        Assert.Equal("abc123", entry!.GrafanaUid);
        Assert.Equal("Existing Dashboard", entry.GrafanaTitle);
        Assert.Equal("General", entry.FolderTitle);
        Assert.Equal(CheckpointStatus.Completed, entry.Status);
        Assert.Equal("cx-1", entry.CxDashboardId);

        Assert.Null(entry.SourcePath);
        Assert.Null(entry.CxFolderId);
        Assert.Null(entry.SourceHash);
    }

    [Fact]
    public async Task SaveAsync_EntryWithNullNewFields_OmitsThemFromJson()
    {
        var file = PathFor("migration-checkpoint.json");
        await File.WriteAllTextAsync(file, LegacyCheckpointJson);

        var store = new CheckpointStore(file);
        await store.LoadAsync();
        await store.SaveAsync();

        var written = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain("SourcePath", written, StringComparison.Ordinal);
        Assert.DoesNotContain("CxFolderId", written, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceHash", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_EntryWithNewFieldsPopulated_RoundTrips()
    {
        var file = PathFor("import-checkpoint.json");
        var store = new CheckpointStore(file);
        store.Upsert("folder-1::uid:abc123", new CheckpointEntry
        {
            GrafanaUid = "abc123",
            GrafanaTitle = "Imported",
            FolderTitle = "DDE - Delivery Data Engineering",
            Status = CheckpointStatus.Completed,
            CxDashboardId = "cx-9",
            SourcePath = "DDE - Delivery Data Engineering - Primary CRM.json",
            CxFolderId = "folder-1",
            SourceHash = "deadbeef"
        });
        await store.SaveAsync();

        var reloaded = new CheckpointStore(file);
        await reloaded.LoadAsync();

        var entry = reloaded.Get("folder-1::uid:abc123");
        Assert.NotNull(entry);
        Assert.Equal("DDE - Delivery Data Engineering - Primary CRM.json", entry!.SourcePath);
        Assert.Equal("folder-1", entry.CxFolderId);
        Assert.Equal("deadbeef", entry.SourceHash);
    }

    [Fact]
    public void Upsert_WithoutKey_StoresUnderGrafanaUid()
    {
        var store = new CheckpointStore(PathFor("c.json"));
        store.Upsert(new CheckpointEntry { GrafanaUid = "uid-1", GrafanaTitle = "T" });

        Assert.NotNull(store.Get("uid-1"));
    }

    [Fact]
    public void Upsert_WithExplicitKey_StoresUnderThatKeyNotTheUid()
    {
        var store = new CheckpointStore(PathFor("c.json"));
        store.Upsert("folder-1::uid:uid-1", new CheckpointEntry { GrafanaUid = "uid-1", GrafanaTitle = "T" });

        Assert.NotNull(store.Get("folder-1::uid:uid-1"));
        Assert.Null(store.Get("uid-1"));
    }

    [Fact]
    public void Upsert_SameUidDifferentFolderKeys_KeepsBothEntries()
    {
        var store = new CheckpointStore(PathFor("c.json"));
        store.Upsert("folder-a::uid:uid-1", new CheckpointEntry
        {
            GrafanaUid = "uid-1", GrafanaTitle = "T", CxDashboardId = "cx-a", CxFolderId = "folder-a"
        });
        store.Upsert("folder-b::uid:uid-1", new CheckpointEntry
        {
            GrafanaUid = "uid-1", GrafanaTitle = "T", CxDashboardId = "cx-b", CxFolderId = "folder-b"
        });

        Assert.Equal(2, store.All.Count);
        Assert.Equal("cx-a", store.Get("folder-a::uid:uid-1")!.CxDashboardId);
        Assert.Equal("cx-b", store.Get("folder-b::uid:uid-1")!.CxDashboardId);
    }
}
