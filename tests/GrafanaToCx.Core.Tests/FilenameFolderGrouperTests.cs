using GrafanaToCx.Core.Migration;

namespace GrafanaToCx.Core.Tests;

public sealed class FilenameFolderGrouperTests
{
    private static readonly FolderGroupingOptions Default = new();

    /// <summary>
    /// A synthetic export set shaped like the directories this grouper runs on. It keeps the
    /// awkward cases a real export set has, without reproducing any real names.
    /// </summary>
    private static readonly string[] SampleExportFileNames =
    [
        "DDE - Delivery Data Engineering - Analytics - Search Ops Monitor.json",
        "DDE - Delivery Data Engineering - Primary CRM.json",
        "DDE - Delivery Data Engineering - Comment Review Monitoring.json",
        "DDM - Delivery Data Modelling - Stream Connect Monitoring.json",
        "YM - Yield Management - Overview.json",
        "YM - Yield Management - Screening Dashboard Main.json",
        "YM - Yield Management - Screening Summary.json",
        "WSP - webShop Identity - IdentityAPI+VerifyApp.json",
        "WSP - webShop Login & Registration - Shared Services Dashboard K8s.json",
        "WSP - webShop Login & Registration - Traffic Graphs K8.json",
        "WSP - webShop Login & Registration - Native Apps K8s.json",
        "WSP - webShop Login & Registration - Regional K8s.json",
        "WSP - webShop Login & Registration - Support Portal K8s.json",
        "WSP - webShop Login & Registration - Signups.json",
        "WSP - webShop Login & Registration - Payments K8s.json",
        "WSP - webShop Platforms - Primary.json",
        "WSP - webShop Platforms - Secondary.json",
        "WSP - WebShop VSM - Customer API.json",
        "WSP - WebShop VSM - Ledger Service.json",
        "WSP - WebShop VSM - Rules Engine.json",
        "WSP - WebShop VSM - Catalog Service.json",
        "WIWO - Wire In Wire Out - Ledger - Recon Monitoring.json",
        "WIWO - Wire In Wire Out - Ledger Feeds Platform Consolidated.json"
    ];

    private static IReadOnlyList<ImportFolderGroup> GroupSample(FolderGroupingOptions? options = null) =>
        FilenameFolderGrouper.Group(SampleExportFileNames, options ?? Default);

    [Fact]
    public void Group_SampleExportFileNames_ProducesEightFoldersWithNoUngrouped()
    {
        var groups = GroupSample();

        var counts = groups.ToDictionary(g => g.FolderName!, g => g.Files.Count, StringComparer.Ordinal);

        Assert.Equal(8, groups.Count);
        Assert.All(groups, g => Assert.NotNull(g.FolderName));
        Assert.Equal(23, groups.Sum(g => g.Files.Count));

        Assert.Equal(3, counts["DDE - Delivery Data Engineering"]);
        Assert.Equal(1, counts["DDM - Delivery Data Modelling"]);
        Assert.Equal(3, counts["YM - Yield Management"]);
        Assert.Equal(1, counts["WSP - webShop Identity"]);
        Assert.Equal(7, counts["WSP - webShop Login & Registration"]);
        Assert.Equal(2, counts["WSP - webShop Platforms"]);
        Assert.Equal(4, counts["WSP - WebShop VSM"]);
        Assert.Equal(2, counts["WIWO - Wire In Wire Out"]);
    }

    [Fact]
    public void Group_SampleExportFileNames_SingleSegment_ProducesFiveFolders()
    {
        var groups = FilenameFolderGrouper.Group(SampleExportFileNames, new FolderGroupingOptions(SegmentCount: 1));

        Assert.Equal(5, groups.Count);
        Assert.Equal(
            ["DDE", "DDM", "WIWO", "WSP", "YM"],
            groups.Select(g => g.FolderName ?? string.Empty).ToArray());
    }

    [Fact]
    public void Group_SampleExportFileNames_ThreeSegments_LeavesMostFilesUngrouped()
    {
        var groups = FilenameFolderGrouper.Group(SampleExportFileNames, new FolderGroupingOptions(SegmentCount: 3));

        var ungrouped = Assert.Single(groups, g => g.FolderName is null);
        Assert.Equal(21, ungrouped.Files.Count);
        Assert.Equal(2, groups.Count(g => g.FolderName is not null));
    }

    [Fact]
    public void Group_MoreSegmentsThanCount_KeepsSeparatorInsideRemainder()
    {
        var groups = FilenameFolderGrouper.Group(
            ["WIWO - Wire In Wire Out - Ledger - Recon Monitoring.json"], Default);

        var file = Assert.Single(Assert.Single(groups).Files);
        Assert.Equal("WIWO - Wire In Wire Out", file.FolderName);
        Assert.Equal("Ledger - Recon Monitoring", file.RemainderName);
    }

    [Fact]
    public void Group_SeparatorMissing_ReturnsUngrouped()
    {
        var groups = FilenameFolderGrouper.Group(["Signups.json"], Default);

        var group = Assert.Single(groups);
        Assert.Null(group.FolderName);
        Assert.Equal("Signups", Assert.Single(group.Files).RemainderName);
    }

    [Fact]
    public void Group_FewerSegmentsThanCount_ReturnsUngrouped()
    {
        var groups = FilenameFolderGrouper.Group(["DDE - Primary CRM.json"], new FolderGroupingOptions(SegmentCount: 3));

        Assert.Null(Assert.Single(groups).FolderName);
    }

    [Fact]
    public void Group_SegmentCountEqualsSegmentTotal_ReturnsUngroupedRatherThanEmptyName()
    {
        var groups = FilenameFolderGrouper.Group(["DDE - Delivery Data Engineering.json"], Default);

        var group = Assert.Single(groups);
        Assert.Null(group.FolderName);
        Assert.Equal("DDE - Delivery Data Engineering", Assert.Single(group.Files).RemainderName);
    }

    [Fact]
    public void Group_AmpersandInFolderSegment_IsPreservedVerbatim()
    {
        var groups = FilenameFolderGrouper.Group(
            ["WSP - webShop Login & Registration - Signups.json"], Default);

        Assert.Equal("WSP - webShop Login & Registration", Assert.Single(groups).FolderName);
    }

    [Fact]
    public void Group_FolderNamesDifferingOnlyByCase_MergeIntoSingleGroup()
    {
        var groups = FilenameFolderGrouper.Group(
            ["WSP - webShop VSM - Ledger Service.json", "WSP - WebShop VSM - Catalog Service.json"], Default);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Files.Count);
        Assert.All(group.Files, f => Assert.Equal(group.FolderName, f.FolderName));
    }

    [Fact]
    public void Group_CaseVariantMerge_RecordsTheAlternativeSpelling()
    {
        var groups = FilenameFolderGrouper.Group(
            ["WSP - webShop VSM - Ledger Service.json", "WSP - WebShop VSM - Catalog Service.json"], Default);

        var group = Assert.Single(groups);
        // "WSP - WebShop VSM" sorts before "WSP - webShop VSM" ordinally, so it wins the display name.
        Assert.Equal("WSP - WebShop VSM", group.FolderName);
        Assert.Equal("WSP - webShop VSM", Assert.Single(group.CaseVariants));
    }

    [Fact]
    public void Group_WhitespaceAroundSegments_IsTrimmedAndCanonicalized()
    {
        var groups = FilenameFolderGrouper.Group(["DDE -  Delivery Data Engineering - Primary CRM.json"], Default);

        Assert.Equal("DDE - Delivery Data Engineering", Assert.Single(groups).FolderName);
    }

    [Fact]
    public void Group_EmptySegmentAfterTrim_ReturnsUngrouped()
    {
        var groups = FilenameFolderGrouper.Group(["DDE -  - Primary CRM.json"], Default);

        Assert.Null(Assert.Single(groups).FolderName);
    }

    [Fact]
    public void Group_UngroupedFolderNameConfigured_AssignsThatFolderName()
    {
        var groups = FilenameFolderGrouper.Group(
            ["Signups.json"],
            new FolderGroupingOptions(UngroupedFolderName: "Imported Dashboards"));

        Assert.Equal("Imported Dashboards", Assert.Single(groups).FolderName);
    }

    [Fact]
    public void Group_NestedRelativePaths_GroupsOnFileNameOnly()
    {
        var groups = FilenameFolderGrouper.Group(
            ["sub/DDE - Delivery Data Engineering - Primary CRM.json"], Default);

        var group = Assert.Single(groups);
        Assert.Equal("DDE - Delivery Data Engineering", group.FolderName);
        Assert.Equal("sub/DDE - Delivery Data Engineering - Primary CRM.json", Assert.Single(group.Files).RelativePath);
    }

    [Fact]
    public void Group_IdenticalFileNamesInDifferentDirectories_ShareGroupButKeepDistinctPaths()
    {
        var groups = FilenameFolderGrouper.Group(
            ["a/DDE - Delivery Data Engineering - X.json", "b/DDE - Delivery Data Engineering - X.json"],
            Default);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(2, group.Files.Select(f => f.RelativePath).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Group_BackslashPaths_AreNormalizedToForwardSlashes()
    {
        var groups = FilenameFolderGrouper.Group(
            [@"sub\DDE - Delivery Data Engineering - Primary CRM.json"], Default);

        Assert.Equal(
            "sub/DDE - Delivery Data Engineering - Primary CRM.json",
            Assert.Single(Assert.Single(groups).Files).RelativePath);
    }

    [Fact]
    public void Group_SeparatorContainingRegexMetacharacters_SplitsLiterally()
    {
        var groups = FilenameFolderGrouper.Group(
            ["Team|Sub|Dashboard.json"],
            new FolderGroupingOptions(Separator: "|", SegmentCount: 2));

        var file = Assert.Single(Assert.Single(groups).Files);
        Assert.Equal("Team|Sub", file.FolderName);
        Assert.Equal("Dashboard", file.RemainderName);
    }

    [Fact]
    public void Group_EmptySeparator_ReturnsEverythingUngroupedWithoutThrowing()
    {
        var groups = FilenameFolderGrouper.Group(
            SampleExportFileNames, new FolderGroupingOptions(Separator: string.Empty));

        var group = Assert.Single(groups);
        Assert.Null(group.FolderName);
        Assert.Equal(23, group.Files.Count);
    }

    [Fact]
    public void Group_ZeroSegmentCount_ReturnsEverythingUngrouped()
    {
        var groups = FilenameFolderGrouper.Group(
            SampleExportFileNames, new FolderGroupingOptions(SegmentCount: 0));

        Assert.Null(Assert.Single(groups).FolderName);
    }

    [Fact]
    public void Group_SegmentStartTwo_UsesTheSecondSegmentAsTheFolder()
    {
        var groups = FilenameFolderGrouper.Group(
            SampleExportFileNames, new FolderGroupingOptions(SegmentCount: 1, SegmentStart: 2));

        Assert.Equal(
            [
                "Delivery Data Engineering", "Delivery Data Modelling", "webShop Identity",
                "webShop Login & Registration", "webShop Platforms", "WebShop VSM", "Wire In Wire Out",
                "Yield Management"
            ],
            groups.Select(g => g.FolderName ?? string.Empty).ToArray());
    }

    [Fact]
    public void Group_SegmentStartTwo_KeepsTheSkippedLeadingSegmentInTheRemainder()
    {
        var groups = FilenameFolderGrouper.Group(
            ["DDE - Delivery Data Engineering - Primary CRM.json"],
            new FolderGroupingOptions(SegmentCount: 1, SegmentStart: 2));

        var file = Assert.Single(Assert.Single(groups).Files);
        Assert.Equal("Delivery Data Engineering", file.FolderName);
        Assert.Equal("DDE - Primary CRM", file.RemainderName);
    }

    [Fact]
    public void Group_SegmentStartOne_MatchesTheDefaultLeadingSegmentBehaviour()
    {
        var explicitStart = FilenameFolderGrouper.Group(
            SampleExportFileNames, new FolderGroupingOptions(SegmentStart: 1));

        Assert.Equal(
            GroupSample().Select(g => g.FolderName ?? string.Empty).ToArray(),
            explicitStart.Select(g => g.FolderName ?? string.Empty).ToArray());
    }

    [Fact]
    public void Group_SegmentWindowRunningPastTheLastSegment_ReturnsUngrouped()
    {
        // Segments 2-3 of a three-segment name would consume the dashboard label entirely.
        var groups = FilenameFolderGrouper.Group(
            ["DDE - Delivery Data Engineering - Primary CRM.json"],
            new FolderGroupingOptions(SegmentCount: 2, SegmentStart: 2));

        var group = Assert.Single(groups);
        Assert.Null(group.FolderName);
        Assert.Equal("DDE - Delivery Data Engineering - Primary CRM", Assert.Single(group.Files).RemainderName);
    }

    [Fact]
    public void Group_SegmentStartBeyondSegmentTotal_ReturnsUngrouped()
    {
        var groups = FilenameFolderGrouper.Group(
            SampleExportFileNames, new FolderGroupingOptions(SegmentCount: 1, SegmentStart: 9));

        Assert.Null(Assert.Single(groups).FolderName);
    }

    [Fact]
    public void Group_ZeroSegmentStart_ReturnsEverythingUngrouped()
    {
        var groups = FilenameFolderGrouper.Group(
            SampleExportFileNames, new FolderGroupingOptions(SegmentStart: 0));

        Assert.Null(Assert.Single(groups).FolderName);
    }

    [Fact]
    public void Group_EmptySegmentInsideTheWindow_ReturnsUngrouped()
    {
        var groups = FilenameFolderGrouper.Group(
            ["DDE -  - Primary CRM - Extra.json"],
            new FolderGroupingOptions(SegmentCount: 1, SegmentStart: 2));

        Assert.Null(Assert.Single(groups).FolderName);
    }

    [Fact]
    public void Group_NonJsonExtension_IsNotStrippedFromTheStem()
    {
        var groups = FilenameFolderGrouper.Group(["DDE - Delivery - Dash.txt"], Default);

        Assert.Equal("Dash.txt", Assert.Single(Assert.Single(groups).Files).RemainderName);
    }

    [Fact]
    public void Group_ResultIsDeterministicallyOrdered_WithUngroupedLast()
    {
        var shuffled = SampleExportFileNames.Reverse().Append("NoSeparatorHere.json").ToList();

        var first = FilenameFolderGrouper.Group(shuffled, Default);
        var second = FilenameFolderGrouper.Group(SampleExportFileNames.Append("NoSeparatorHere.json").ToList(), Default);

        Assert.Equal(
            first.Select(g => g.FolderName ?? string.Empty).ToArray(),
            second.Select(g => g.FolderName ?? string.Empty).ToArray());
        Assert.Null(first[^1].FolderName);
    }

    [Fact]
    public void Group_DuplicateRelativePaths_AreDeduplicated()
    {
        var groups = FilenameFolderGrouper.Group(
            ["DDE - Delivery Data Engineering - Primary CRM.json", "DDE - Delivery Data Engineering - Primary CRM.json"],
            Default);

        Assert.Single(Assert.Single(groups).Files);
    }
}
