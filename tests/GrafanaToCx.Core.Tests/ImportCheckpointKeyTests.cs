using GrafanaToCx.Core.Migration;

namespace GrafanaToCx.Core.Tests;

public sealed class ImportCheckpointKeyTests
{
    [Fact]
    public void ResolveKeys_FileWithUid_UsesUidIdentity()
    {
        var keys = ImportCheckpointKey.ResolveKeys([new ImportKeyCandidate("A.json", "uid-1", "folder-1")]);

        Assert.Equal("folder-1::uid:uid-1", keys["A.json"]);
    }

    [Fact]
    public void ResolveKeys_FileWithoutUid_FallsBackToPathIdentity()
    {
        var keys = ImportCheckpointKey.ResolveKeys([new ImportKeyCandidate("A.json", null, "folder-1")]);

        Assert.Equal("folder-1::path:A.json", keys["A.json"]);
    }

    [Fact]
    public void ResolveKeys_FileWithWhitespaceUid_FallsBackToPathIdentity()
    {
        var keys = ImportCheckpointKey.ResolveKeys([new ImportKeyCandidate("A.json", "   ", "folder-1")]);

        Assert.Equal("folder-1::path:A.json", keys["A.json"]);
    }

    [Fact]
    public void ResolveKeys_NullFolderId_UsesStableNoneSentinel()
    {
        var keys = ImportCheckpointKey.ResolveKeys([new ImportKeyCandidate("A.json", "uid-1", null)]);

        Assert.Equal("(none)::uid:uid-1", keys["A.json"]);
    }

    [Fact]
    public void ResolveKeys_EmptyFolderId_IsTreatedAsNoFolder()
    {
        var keys = ImportCheckpointKey.ResolveKeys([new ImportKeyCandidate("A.json", "uid-1", "")]);

        Assert.Equal("(none)::uid:uid-1", keys["A.json"]);
    }

    /// <summary>
    /// The failure this composite key exists to prevent: a bare-uid key would make the second run
    /// overwrite the first run's entry, so folder A's dashboard becomes folder B's replace target.
    /// </summary>
    [Fact]
    public void ResolveKeys_SameUidDifferentTargetFolders_ProducesDistinctKeys()
    {
        var keys = ImportCheckpointKey.ResolveKeys(
        [
            new ImportKeyCandidate("A.json", "uid-1", "folder-a"),
            new ImportKeyCandidate("B.json", "uid-1", "folder-b")
        ]);

        Assert.Equal("folder-a::uid:uid-1", keys["A.json"]);
        Assert.Equal("folder-b::uid:uid-1", keys["B.json"]);
        Assert.NotEqual(keys["A.json"], keys["B.json"]);
    }

    [Fact]
    public void ResolveKeys_DuplicateUidInSameFolder_DemotesAllOccurrencesToPathIdentity()
    {
        var keys = ImportCheckpointKey.ResolveKeys(
        [
            new ImportKeyCandidate("A.json", "shared", "folder-1"),
            new ImportKeyCandidate("B.json", "shared", "folder-1")
        ]);

        // Both, not just the second — otherwise identity depends on enumeration order.
        Assert.Equal("folder-1::path:A.json", keys["A.json"]);
        Assert.Equal("folder-1::path:B.json", keys["B.json"]);
    }

    [Fact]
    public void ResolveKeys_DuplicateUidInSameFolder_DoesNotDemoteUnrelatedUids()
    {
        var keys = ImportCheckpointKey.ResolveKeys(
        [
            new ImportKeyCandidate("A.json", "shared", "folder-1"),
            new ImportKeyCandidate("B.json", "shared", "folder-1"),
            new ImportKeyCandidate("C.json", "unique", "folder-1")
        ]);

        Assert.Equal("folder-1::uid:unique", keys["C.json"]);
    }

    [Fact]
    public void ResolveKeys_DemotionIsIndependentOfEnumerationOrder()
    {
        ImportKeyCandidate[] forward =
        [
            new("A.json", "shared", "folder-1"),
            new("B.json", "shared", "folder-1")
        ];

        var first = ImportCheckpointKey.ResolveKeys(forward);
        var second = ImportCheckpointKey.ResolveKeys(forward.Reverse().ToList());

        Assert.Equal(first["A.json"], second["A.json"]);
        Assert.Equal(first["B.json"], second["B.json"]);
    }

    [Fact]
    public void ResolveKeys_PathIdentity_NormalizesDirectorySeparators()
    {
        var keys = ImportCheckpointKey.ResolveKeys([new ImportKeyCandidate(@"sub\A.json", null, "folder-1")]);

        Assert.Equal("folder-1::path:sub/A.json", keys["sub/A.json"]);
    }

    [Fact]
    public void ResolveKeys_SameFileSameFolder_IsStableAcrossRuns()
    {
        ImportKeyCandidate[] candidates = [new("DDE - Delivery Data Engineering - Primary CRM.json", "pe3J2H2Dk", "folder-1")];

        var first = ImportCheckpointKey.ResolveKeys(candidates);
        var second = ImportCheckpointKey.ResolveKeys(candidates);

        Assert.Equal(first.Single().Value, second.Single().Value);
    }

    [Fact]
    public void ResolveKeys_HandWrittenUid_IsUsedLikeAnyOther()
    {
        // WIWO - Wire In Wire Out - Ledger - Recon Monitoring.json ships a hand-authored uid.
        var keys = ImportCheckpointKey.ResolveKeys(
            [new ImportKeyCandidate("WIWO - Wire In Wire Out - Ledger - Recon Monitoring.json", "LedgerReconMonitoring", "folder-1")]);

        Assert.Equal("folder-1::uid:LedgerReconMonitoring", keys.Single().Value);
    }

    [Fact]
    public void ResolveKeys_SameUidAcrossFoldersIsNotTreatedAsAContest()
    {
        var keys = ImportCheckpointKey.ResolveKeys(
        [
            new ImportKeyCandidate("A.json", "uid-1", "folder-a"),
            new ImportKeyCandidate("B.json", "uid-1", "folder-b")
        ]);

        // Folder already disambiguates, so uid identity is still safe.
        Assert.All(keys.Values, key => Assert.Contains("uid:uid-1", key, StringComparison.Ordinal));
    }
}
