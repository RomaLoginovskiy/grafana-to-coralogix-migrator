using GrafanaToCx.Core.ApiClient;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Search results shaped like Grafana 13's api/search response, which is where folder ids
/// stopped fitting an int and folder filters stopped being portable.
/// </summary>
public class GrafanaClientSearchTests
{
    private static readonly JArray Grafana13Results = JArray.Parse("""
        [
          {"id":2682840631361536,"uid":"a976v4","title":"Node CPU","type":"dash-db",
           "folderId":2682840189992960,"folderUid":"ag17itxtx5urkb","folderTitle":"Ops"},
          {"id":2682840631361537,"uid":"b123","title":"Root Board","type":"dash-db"},
          {"id":2682840631361538,"uid":"c456","title":"Nested","type":"dash-db",
           "folderUid":"child1","folderTitle":"Alpha"},
          {"id":2682840631361539,"uid":"d789","title":"Disk","type":"dash-db",
           "folderUid":"ag17itxtx5urkb","folderTitle":"Ops"}
        ]
        """);

    [Fact]
    public void ParseSearchHits_RootDashboardsBelongToGeneral()
    {
        var hits = GrafanaClient.ParseSearchHits(Grafana13Results);

        var root = Assert.Single(hits, h => h.Dashboard.Uid == "b123");
        Assert.Equal(string.Empty, root.FolderUid);
        Assert.Equal("General", root.Dashboard.FolderTitle);
    }

    [Fact]
    public void ParseSearchHits_SkipsResultsWithoutUid()
    {
        var hits = GrafanaClient.ParseSearchHits(JArray.Parse("""[{"title":"no uid"}]"""));

        Assert.Empty(hits);
    }

    [Fact]
    public void GroupFolders_OneFolderPerUid_GeneralFirstThenByTitle()
    {
        var folders = GrafanaClient.GroupFolders(GrafanaClient.ParseSearchHits(Grafana13Results));

        Assert.Equal(
            [new GrafanaFolder("", "General"), new GrafanaFolder("child1", "Alpha"), new GrafanaFolder("ag17itxtx5urkb", "Ops")],
            folders);
    }
}
