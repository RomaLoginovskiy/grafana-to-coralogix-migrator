using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.ApiClient;

public interface IGrafanaClient
{
    Task<List<GrafanaFolder>> GetFoldersAsync(IReadOnlyList<string> folderFilter, CancellationToken ct = default);
    Task<List<GrafanaDashboardRef>> GetDashboardsInFolderAsync(string folderUid, CancellationToken ct = default);
    Task<JObject?> GetDashboardByUidAsync(string uid, CancellationToken ct = default);
}

/// <summary>A Grafana folder. The root ("General") folder has an empty <paramref name="Uid"/>.</summary>
public sealed record GrafanaFolder(string Uid, string Title);
public sealed record GrafanaDashboardRef(string Uid, string Title, string FolderTitle);
