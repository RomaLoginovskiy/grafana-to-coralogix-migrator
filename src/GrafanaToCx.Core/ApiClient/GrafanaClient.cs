using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.ApiClient;

/// <summary>
/// Lists dashboards with a single unfiltered <c>api/search</c> and groups them by folder locally.
/// Grafana's folder filters are not portable: <c>folderIds</c> returns nothing on Grafana 13 (whose
/// ids also overflow an int), while older versions may ignore <c>folderUIDs</c> and return every
/// dashboard. <c>api/folders</c> also lists only top-level folders once nested folders exist.
/// Grouping the search results ourselves works on all of them and includes nested folders.
/// </summary>
public sealed class GrafanaClient : IGrafanaClient, IDisposable
{
    public const string GeneralFolderTitle = "General";

    // Grafana's maximum page size for api/search.
    private const int SearchPageSize = 5000;

    private readonly HttpClient _httpClient;
    private readonly ILogger<GrafanaClient> _logger;
    private List<GrafanaSearchHit>? _searchHits;

    /// <param name="tlsSkipVerify">
    /// Accept any server certificate. For self-hosted Grafana behind a self-signed or private-CA
    /// cert; this also accepts an impostor, so it is opt-in and only ever applied to Grafana.
    /// </param>
    public GrafanaClient(ILogger<GrafanaClient> logger, string baseUrl, string apiKey, bool tlsSkipVerify = false)
    {
        _logger = logger;

        var handler = new HttpClientHandler();
        if (tlsSkipVerify)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            _logger.LogWarning("TLS certificate verification is disabled for Grafana at {BaseUrl}", baseUrl);
        }

        _httpClient = new HttpClient(handler) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        // No key means anonymous access. Sending a bad key isn't equivalent: with anonymous access
        // enabled, Grafana 10+ falls back to anonymous but 9.x answers 401.
        if (!string.IsNullOrEmpty(apiKey))
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    /// <summary>
    /// Folders that contain at least one dashboard; empty folders have nothing to migrate.
    /// Throws when Grafana refuses the search, so a bad key isn't mistaken for an empty Grafana.
    /// </summary>
    public async Task<List<GrafanaFolder>> GetFoldersAsync(
        IReadOnlyList<string> folderFilter,
        CancellationToken ct = default)
    {
        var hits = await GetSearchHitsAsync(ct);
        var folders = GroupFolders(hits);

        if (folderFilter.Count > 0)
        {
            var filter = new HashSet<string>(folderFilter, StringComparer.OrdinalIgnoreCase);
            folders = folders.Where(f => filter.Contains(f.Title)).ToList();
        }

        _logger.LogInformation("Found {Count} Grafana folders", folders.Count);
        return folders;
    }

    public async Task<List<GrafanaDashboardRef>> GetDashboardsInFolderAsync(
        string folderUid,
        CancellationToken ct = default)
    {
        var hits = await GetSearchHitsAsync(ct);
        return hits.Where(h => h.FolderUid == folderUid).Select(h => h.Dashboard).ToList();
    }

    public async Task<JObject?> GetDashboardByUidAsync(string uid, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"api/dashboards/uid/{uid}", ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to fetch dashboard {Uid}. Status: {Status}", uid, response.StatusCode);
            return null;
        }

        return JObject.Parse(content);
    }

    /// <summary>
    /// All dashboards, fetched once per client. A failure is not cached, so a later call retries.
    /// </summary>
    private async Task<List<GrafanaSearchHit>> GetSearchHitsAsync(CancellationToken ct)
    {
        if (_searchHits is not null)
            return _searchHits;

        var hits = new List<GrafanaSearchHit>();
        var seen = new HashSet<string>();

        for (var page = 1; ; page++)
        {
            var response = await _httpClient.GetAsync($"api/search?type=dash-db&limit={SearchPageSize}&page={page}", ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to search Grafana dashboards. Status: {Status}. Response: {Response}",
                    response.StatusCode, content);
                throw new HttpRequestException(
                    $"Grafana search returned {(int)response.StatusCode} {response.StatusCode}: {content.Trim()}",
                    null,
                    response.StatusCode);
            }

            var batch = ParseSearchHits(JArray.Parse(content));
            var added = 0;
            foreach (var hit in batch.Where(h => seen.Add(h.Dashboard.Uid)))
            {
                hits.Add(hit);
                added++;
            }

            // A Grafana that ignores page= returns page 1 again; nothing new means we're done either way.
            if (batch.Count < SearchPageSize || added == 0)
                break;
        }

        _logger.LogInformation("Found {Count} Grafana dashboards", hits.Count);
        _searchHits = hits;
        return hits;
    }

    public static List<GrafanaSearchHit> ParseSearchHits(JArray results) =>
        results
            .Select(item =>
            {
                // Root-level dashboards carry no folderUid/folderTitle at all.
                var folderUid = item.Value<string>("folderUid") ?? string.Empty;
                var folderTitle = folderUid.Length == 0
                    ? GeneralFolderTitle
                    : item.Value<string>("folderTitle") ?? folderUid;

                return new GrafanaSearchHit(
                    new GrafanaDashboardRef(
                        item.Value<string>("uid") ?? string.Empty,
                        item.Value<string>("title") ?? string.Empty,
                        folderTitle),
                    folderUid);
            })
            .Where(h => !string.IsNullOrEmpty(h.Dashboard.Uid))
            .ToList();

    /// <summary>General first, then the rest by title.</summary>
    public static List<GrafanaFolder> GroupFolders(IEnumerable<GrafanaSearchHit> hits) =>
        hits
            .GroupBy(h => h.FolderUid)
            .Select(g => new GrafanaFolder(g.Key, g.First().Dashboard.FolderTitle))
            .OrderBy(f => f.Uid.Length == 0 ? 0 : 1)
            .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void Dispose() => _httpClient.Dispose();
}

public sealed record GrafanaSearchHit(GrafanaDashboardRef Dashboard, string FolderUid);
