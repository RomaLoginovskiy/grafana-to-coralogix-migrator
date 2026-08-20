using GrafanaToCx.Cli.Cli;

namespace GrafanaToCx.Core.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cx-sessions-{Guid.NewGuid():N}");
    private readonly List<string> _warnings = [];

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private SessionStore Store() => new(_dir, _warnings.Add);

    private string PathFor(string id) => Path.Combine(_dir, $"{id}.json");

    /// <summary>
    /// A session file written before later fields existed. Adding a remembered answer must not stop older
    /// files from loading, or an upgrade would silently discard everything an operator had accumulated.
    /// </summary>
    private const string LegacySessionJson = """
    {
      "Id": "aaaa1111",
      "CreatedAt": "2026-08-01T09:00:00+00:00",
      "LastUsedAt": "2026-08-01T09:30:00+00:00",
      "Region": "eu2"
    }
    """;

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_ThenList_RoundTripsThroughAFreshStore()
    {
        var store = Store();
        var session = store.Create();
        session.GrafanaImportRootDir = "/dashboards";
        session.GrafanaImportRegion = "eu2";
        session.GrafanaImportRecursive = true;
        session.GrafanaImportDryRun = false;

        await store.SaveAsync(session);

        var reloaded = new SessionStore(_dir, _warnings.Add).Resolve(session.Id, out _);

        Assert.NotNull(reloaded);
        Assert.Equal("/dashboards", reloaded.GrafanaImportRootDir);
        Assert.Equal("eu2", reloaded.GrafanaImportRegion);
        Assert.True(reloaded.GrafanaImportRecursive);
        Assert.False(reloaded.GrafanaImportDryRun);
    }

    /// <summary>
    /// "Never answered" and "answered no" must stay distinct, or a fresh session's default becomes
    /// indistinguishable from a deliberate opt-out.
    /// </summary>
    [Fact]
    public async Task SaveAsync_UnansweredConfirms_RoundTripAsNull()
    {
        var store = Store();
        var session = store.Create();

        await store.SaveAsync(session);
        var reloaded = store.Resolve(session.Id, out _);

        Assert.NotNull(reloaded);
        Assert.Null(reloaded.GrafanaImportRecursive);
        Assert.Null(reloaded.GrafanaImportDryRun);
    }

    [Fact]
    public async Task LoadingALegacyFile_MissingNewerFields_SucceedsWithNulls()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(PathFor("aaaa1111"), LegacySessionJson);

        var session = Store().Resolve("aaaa1111", out _);

        Assert.NotNull(session);
        Assert.Equal("eu2", session.Region);
        Assert.Null(session.GrafanaImportRootDir);
        Assert.Null(session.GrafanaImportDryRun);
        Assert.Empty(_warnings);
    }

    // ── Credentials must never reach disk ─────────────────────────────────────

    /// <summary>
    /// The regression that matters most. A session file lives in the operator's home directory and is
    /// designed to be reopened later; a credential in it would be a secret at rest that nobody asked for.
    /// Resuming re-asks for the key instead.
    /// </summary>
    [Fact]
    public async Task SaveAsync_NeverWritesACredential()
    {
        const string secret = "cxup_super_secret_key_value";

        var store = Store();
        var session = store.Create();
        session.Region = "eu2";
        session.GrafanaImportRootDir = "/dashboards";

        await store.SaveAsync(session);

        var raw = await File.ReadAllTextAsync(PathFor(session.Id));

        Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("cxup_", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ── Resilience ────────────────────────────────────────────────────────────

    [Fact]
    public void List_MissingDirectory_ReturnsEmptyWithoutThrowing()
    {
        Assert.Empty(Store().List());
        Assert.Null(Store().MostRecent());
    }

    /// <summary>
    /// Unlike the checkpoint, a corrupt session file must not stop the console starting: it records
    /// convenience, not what has already been published.
    /// </summary>
    [Fact]
    public async Task List_CorruptFile_IsSkippedWithAWarning()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(PathFor("bbbb2222"), "{ this is not json");

        var store = Store();
        var sessions = store.List();

        Assert.Empty(sessions);
        Assert.Contains(_warnings, w => w.Contains("bbbb2222", StringComparison.Ordinal));
    }

    [Fact]
    public async Task List_CorruptFileAlongsideAGoodOne_StillReturnsTheGoodOne()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(PathFor("bbbb2222"), "{ this is not json");
        await File.WriteAllTextAsync(PathFor("aaaa1111"), LegacySessionJson);

        var sessions = Store().List();

        Assert.Single(sessions);
        Assert.Equal("aaaa1111", sessions[0].Id);
    }

    /// <summary>
    /// The stem is not trusted as the id: a file renamed on disk would otherwise resolve under a name its
    /// own contents disagree with, and saving it back would write to a third path.
    /// </summary>
    [Fact]
    public async Task List_FileWithoutAnId_IsSkipped()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(PathFor("cccc3333"), """{ "Region": "eu1" }""");

        Assert.Empty(Store().List());
        Assert.Contains(_warnings, w => w.Contains("no session id", StringComparison.Ordinal));
    }

    // ── Ordering and pruning ──────────────────────────────────────────────────

    [Fact]
    public async Task List_OrdersByLastUsedNewestFirst()
    {
        var store = Store();

        var older = store.Create();
        await store.SaveAsync(older);
        older.LastUsedAt = DateTimeOffset.UtcNow.AddDays(-3);
        await File.WriteAllTextAsync(PathFor(older.Id),
            Newtonsoft.Json.JsonConvert.SerializeObject(older));

        var newer = store.Create();
        await store.SaveAsync(newer);

        var sessions = store.List();

        Assert.Equal(newer.Id, sessions[0].Id);
        Assert.Equal(newer.Id, store.MostRecent()!.Id);
    }

    [Fact]
    public async Task SaveAsync_KeepsOnlyTheTwentyMostRecentSessions()
    {
        var store = Store();
        Directory.CreateDirectory(_dir);

        // Written oldest-first with distinct LastUsedAt so pruning has an unambiguous order to work from.
        for (var i = 0; i < 25; i++)
        {
            var session = store.Create();
            session.LastUsedAt = DateTimeOffset.UtcNow.AddMinutes(-(25 - i));
            await File.WriteAllTextAsync(PathFor(session.Id),
                Newtonsoft.Json.JsonConvert.SerializeObject(session));
        }

        Assert.Equal(25, store.List().Count);

        // Any save triggers the prune.
        await store.SaveAsync(store.Create());

        Assert.Equal(20, store.List().Count);
    }

    // ── Id resolution ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_UnambiguousPrefix_FindsTheSession()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(PathFor("aaaa1111"), LegacySessionJson);

        var session = Store().Resolve("aaaa", out var ambiguous);

        Assert.NotNull(session);
        Assert.Equal("aaaa1111", session.Id);
        Assert.Empty(ambiguous);
    }

    [Fact]
    public async Task Resolve_AmbiguousPrefix_ReturnsNullAndNamesTheCandidates()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(PathFor("aaaa1111"), LegacySessionJson);
        await File.WriteAllTextAsync(PathFor("aaaa2222"),
            LegacySessionJson.Replace("aaaa1111", "aaaa2222", StringComparison.Ordinal));

        var session = Store().Resolve("aaaa", out var ambiguous);

        Assert.Null(session);
        Assert.Equal(2, ambiguous.Count);
        Assert.Contains("aaaa1111", ambiguous);
        Assert.Contains("aaaa2222", ambiguous);
    }

    /// <summary>An id that happens to prefix another must still be addressable in full.</summary>
    [Fact]
    public async Task Resolve_ExactMatch_BeatsALongerIdItPrefixes()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(PathFor("aaaa"),
            LegacySessionJson.Replace("aaaa1111", "aaaa", StringComparison.Ordinal));
        await File.WriteAllTextAsync(PathFor("aaaa1111"), LegacySessionJson);

        var session = Store().Resolve("aaaa", out var ambiguous);

        Assert.NotNull(session);
        Assert.Equal("aaaa", session.Id);
        Assert.Empty(ambiguous);
    }

    [Theory]
    [InlineData("zzzz")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Resolve_NoMatch_ReturnsNullWithoutClaimingAmbiguity(string query)
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(PathFor("aaaa1111"), LegacySessionJson);

        var session = Store().Resolve(query, out var ambiguous);

        Assert.Null(session);
        Assert.Empty(ambiguous);
    }

    [Fact]
    public async Task Resolve_IsCaseInsensitive()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(PathFor("aaaa1111"), LegacySessionJson);

        Assert.NotNull(Store().Resolve("AAAA1111", out _));
    }

    // ── Create ────────────────────────────────────────────────────────────────

    /// <summary>Starting the console and quitting immediately should not litter the directory.</summary>
    [Fact]
    public void Create_DoesNotTouchDisk()
    {
        var session = Store().Create();

        Assert.False(File.Exists(PathFor(session.Id)));
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Create_ProducesDistinctShortIds()
    {
        var store = Store();
        var ids = Enumerable.Range(0, 50).Select(_ => store.Create().Id).ToList();

        Assert.All(ids, id => Assert.Equal(8, id.Length));
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DefaultRootDirectory_IsUnderTheUsersHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.StartsWith(home, SessionStore.DefaultRootDirectory(), StringComparison.Ordinal);
        Assert.Contains(".grafana-to-cx", SessionStore.DefaultRootDirectory(), StringComparison.Ordinal);
    }
}
