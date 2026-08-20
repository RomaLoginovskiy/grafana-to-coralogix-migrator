using GrafanaToCx.Core.ApiClient;
using GrafanaToCx.Core.Migration;

namespace GrafanaToCx.Core.Tests;

public sealed class ExistingFolderMatcherTests
{
    private static TargetFolder Folder(string name, string? id = null, string? parentId = null) =>
        new(id ?? name.ToLowerInvariant().Replace(' ', '-'), name, parentId);

    [Fact]
    public void Match_ExactName_IsReportedAsExact()
    {
        var match = ExistingFolderMatcher.Match("Yield Management", [Folder("Yield Management")]);

        Assert.Equal(FolderMatchKind.Exact, match.Kind);
        Assert.Equal("Yield Management", match.Folder?.Name);
    }

    [Fact]
    public void Match_NameDifferingOnlyByCase_IsExact()
    {
        var match = ExistingFolderMatcher.Match("yield management", [Folder("Yield Management")]);

        Assert.Equal(FolderMatchKind.Exact, match.Kind);
    }

    [Fact]
    public void Match_NameDifferingOnlyByPunctuation_IsNormalized()
    {
        var match = ExistingFolderMatcher.Match("Wire In / Wire Out", [Folder("wire-in-wire-out")]);

        Assert.Equal(FolderMatchKind.Normalized, match.Kind);
        Assert.Equal("wire-in-wire-out", match.Folder?.Name);
    }

    /// <summary>
    /// The case that motivated this: filename-derived groups carry a team prefix the destination folder
    /// does not have, so the destination's exact-match get-or-create would create a duplicate.
    /// </summary>
    [Fact]
    public void Match_GroupNameCarryingAPrefix_MatchesTheContainedFolderName()
    {
        var match = ExistingFolderMatcher.Match(
            "DDE - Delivery Data Engineering",
            [Folder("Delivery Data Engineering")]);

        Assert.Equal(FolderMatchKind.Contains, match.Kind);
        Assert.Equal("Delivery Data Engineering", match.Folder?.Name);
    }

    [Fact]
    public void Match_ExactCandidate_WinsOverAContainedOne()
    {
        var match = ExistingFolderMatcher.Match(
            "WSP - webShop Platforms",
            [Folder("webShop Platforms"), Folder("WSP - webShop Platforms")]);

        Assert.Equal(FolderMatchKind.Exact, match.Kind);
        Assert.Equal("WSP - webShop Platforms", match.Folder?.Name);
    }

    [Fact]
    public void Match_SeveralContainedCandidates_PicksTheMostSpecific()
    {
        var match = ExistingFolderMatcher.Match(
            "DDE - Delivery Data Engineering",
            [Folder("Delivery"), Folder("Delivery Data Engineering"), Folder("Data")]);

        Assert.Equal("Delivery Data Engineering", match.Folder?.Name);
    }

    /// <summary>
    /// Without a length floor, a two-letter folder would claim nearly every group.
    /// </summary>
    [Fact]
    public void Match_ShortFolderName_DoesNotWinOnContainment()
    {
        var match = ExistingFolderMatcher.Match("YM - Yield Management", [Folder("YM")]);

        Assert.Equal(FolderMatchKind.None, match.Kind);
        Assert.Null(match.Folder);
    }

    [Fact]
    public void Match_NoCandidateIsClose_ReturnsNone()
    {
        var match = ExistingFolderMatcher.Match("WIWO - Wire In Wire Out", [Folder("Observability")]);

        Assert.Equal(FolderMatchKind.None, match.Kind);
        Assert.Null(match.Folder);
    }

    [Fact]
    public void Match_NoFoldersExist_ReturnsNone()
    {
        var match = ExistingFolderMatcher.Match("Anything", []);

        Assert.Equal(FolderMatchKind.None, match.Kind);
    }

    [Fact]
    public void Match_GroupNameOfPurePunctuation_ReturnsNoneRatherThanMatchingEverything()
    {
        var match = ExistingFolderMatcher.Match(" - - ", [Folder("Delivery Data Engineering")]);

        Assert.Equal(FolderMatchKind.None, match.Kind);
    }

    [Fact]
    public void Match_TiedCandidates_ResolveDeterministicallyRegardlessOfInputOrder()
    {
        TargetFolder[] folders = [Folder("Alpha Team", "id-a"), Folder("Bravo Team", "id-b")];

        var first = ExistingFolderMatcher.Match("Team", folders);
        var second = ExistingFolderMatcher.Match("Team", [.. folders.Reverse()]);

        Assert.Equal(first.Folder?.Id, second.Folder?.Id);
    }

    [Fact]
    public void MatchAll_ReturnsOneResultPerGroupInOrder()
    {
        var matches = ExistingFolderMatcher.MatchAll(
            ["DDE - Delivery Data Engineering", "Nothing Like This"],
            [Folder("Delivery Data Engineering")]);

        Assert.Equal(2, matches.Count);
        Assert.Equal(FolderMatchKind.Contains, matches[0].Kind);
        Assert.Equal(FolderMatchKind.None, matches[1].Kind);
        Assert.Equal("Nothing Like This", matches[1].GroupName);
    }
}
