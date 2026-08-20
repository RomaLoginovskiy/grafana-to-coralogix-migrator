using System.Net;
using System.Net.Http.Headers;
using System.Text;
using GrafanaToCx.Core.GrafanaToGrafana;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.ApiClient;

public sealed class GrafanaPublishOptions
{
    /// <summary>Recorded in the destination's dashboard version history, so a reader can see where it came from.</summary>
    public string Message { get; init; } = "Imported by grafana-to-cx";
}

/// <summary>
/// Reads and writes dashboards, folders and datasources on a Grafana instance.
/// </summary>
/// <remarks>
/// Implements both ports because they address the same server over the same connection with the same
/// credential; splitting them would mean two clients and two auth setups for one API.
/// <para>
/// Dashboards are always saved with <c>overwrite: true</c>. That is not a policy knob: with
/// <c>overwrite: false</c> Grafana rejects the save whenever the stored version differs from the one sent,
/// and since the transform deliberately omits <c>version</c>, every update to an existing dashboard would
/// fail. Whether an already-imported dashboard is revisited at all is decided upstream by the checkpoint,
/// not here.
/// </para>
/// </remarks>
public sealed class GrafanaApiClient : IDashboardPublisher, IDashboardFolderTarget, IDisposable
{
    private const int PageSize = 500;

    private readonly HttpClient _httpClient;
    private readonly ILogger<GrafanaApiClient> _logger;
    private readonly GrafanaPublishOptions _options;
    private readonly bool _ownsHttpClient;

    /// <param name="handler">
    /// Supplied only by tests, which drive the client with a stub transport. Production callers leave it
    /// null and get the default handler.
    /// </param>
    public GrafanaApiClient(
        ILogger<GrafanaApiClient> logger,
        string baseUrl,
        string apiKey,
        GrafanaPublishOptions? options = null,
        HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _options = options ?? new GrafanaPublishOptions();
        _ownsHttpClient = handler is null;

        // AllowAutoRedirect is off deliberately. The Coralogix gateway answers an unauthenticated (or
        // wrongly-authenticated) /grafana call with 302 to the SPA login route rather than 401; following
        // that redirect yields 200 with the HTML shell's "OK" body, so an auth failure arrived here
        // disguised as a JSON parse error on a successful response. Left unfollowed, the 302 reaches the
        // status checks below and is reported as what it is.
        _httpClient = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        _httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string TargetDisplayName => "Grafana";

    /// <summary>
    /// False because the Coralogix-hosted Grafana runs without the <c>nestedFolders</c> feature toggle,
    /// so every folder is top-level and a parent selection could not be honoured.
    /// </summary>
    public bool SupportsNestedFolders => false;

    // ── Folders ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TargetFolder>> ListFoldersAsync(CancellationToken ct = default)
    {
        var folders = new List<TargetFolder>();

        // Paged even though a typical instance holds far fewer than one page: Grafana silently truncates
        // at its default limit, and a truncated list turns "folder exists" into "create a duplicate".
        for (var page = 1; ; page++)
        {
            var response = await _httpClient.GetAsync($"api/folders?limit={PageSize}&page={page}", ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to list Grafana folders. {Failure}", DescribeFailure(response, content));
                return folders;
            }

            var batch = ParseArray(content);
            if (batch is null || batch.Count == 0) break;

            folders.AddRange(batch
                .OfType<JObject>()
                // The uid, never the numeric id: folderUid is what the dashboard save API accepts.
                .Select(f => new TargetFolder(f.Value<string>("uid") ?? string.Empty, f.Value<string>("title") ?? string.Empty))
                .Where(f => !string.IsNullOrEmpty(f.Id)));

            if (batch.Count < PageSize) break;
        }

        return folders;
    }

    public async Task<string?> GetOrCreateFolderAsync(
        string name, string? parentId = null, CancellationToken ct = default)
    {
        if (parentId is not null)
        {
            _logger.LogWarning(
                "Ignoring parent folder '{ParentId}' — this Grafana does not support nested folders.", parentId);
        }

        var existing = (await ListFoldersAsync(ct))
            .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null) return existing.Id;

        var payload = new JObject { ["title"] = name };
        var response = await _httpClient.PostAsync("api/folders", JsonContent(payload), ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to create Grafana folder '{Name}'. {Failure}",
                name, DescribeFailure(response, content));
            return null;
        }

        var uid = ParseObject(content)?.Value<string>("uid");
        if (string.IsNullOrEmpty(uid))
        {
            _logger.LogError("Grafana accepted folder '{Name}' but returned no uid. Response: {Response}",
                name, Truncate(content));
            return null;
        }

        _logger.LogInformation("Created Grafana folder '{Name}' (uid: {Uid}).", name, uid);
        return uid;
    }

    // ── Dashboards ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TargetDashboard>> GetCatalogAsync(CancellationToken ct = default)
    {
        var dashboards = new List<TargetDashboard>();

        for (var page = 1; ; page++)
        {
            var response = await _httpClient.GetAsync(
                $"api/search?type=dash-db&limit={PageSize}&page={page}", ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to read the Grafana dashboard catalog. {Failure}",
                    DescribeFailure(response, content));
                return dashboards;
            }

            var batch = ParseArray(content);
            if (batch is null || batch.Count == 0) break;

            dashboards.AddRange(batch
                .OfType<JObject>()
                .Select(d => new TargetDashboard(
                    d.Value<string>("uid") ?? string.Empty,
                    d.Value<string>("title") ?? string.Empty,
                    // Empty folderUid means the General folder, which the plan represents as no folder.
                    string.IsNullOrEmpty(d.Value<string>("folderUid")) ? null : d.Value<string>("folderUid")))
                .Where(d => !string.IsNullOrEmpty(d.Id)));

            if (batch.Count < PageSize) break;
        }

        return dashboards;
    }

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct = default)
    {
        var payload = new JObject
        {
            ["dashboard"] = request.Dashboard,
            ["overwrite"] = true,
            ["message"] = _options.Message
        };

        // Omitted rather than sent empty: an absent folderUid means the General folder, whereas
        // "folderUid": "" is rejected. folderId is deprecated and never sent.
        if (!string.IsNullOrWhiteSpace(request.FolderId))
            payload["folderUid"] = request.FolderId;

        HttpResponseMessage response;
        string content;
        try
        {
            response = await _httpClient.PostAsync("api/dashboards/db", JsonContent(payload), ct);
            content = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            return PublishResult.NetworkError($"request failed: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return PublishResult.NetworkError($"request timed out: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            return PublishResult.Failed(
                response.StatusCode,
                $"Grafana returned {DescribeFailure(response, content)}",
                ReadRetryAfter(response));
        }

        var uid = ParseObject(content)?.Value<string>("uid");
        if (string.IsNullOrEmpty(uid))
        {
            return PublishResult.Failed(response.StatusCode,
                $"Grafana accepted the dashboard but returned no uid. Response: {Truncate(content)}");
        }

        _logger.LogInformation("Dashboard '{Name}' published — Grafana uid: {Uid}.", request.DashboardName, uid);
        return PublishResult.Succeeded(uid);
    }

    // ── Datasources ───────────────────────────────────────────────────────────

    /// <remarks>
    /// Two routes, because <c>GET /api/datasources</c> is admin-only: Grafana grants
    /// <c>datasources:read</c> over <c>datasources:*</c> to the Admin role alone, so a Viewer or Editor
    /// credential — which is what a Coralogix-hosted Grafana hands out — gets 403 even though it can query
    /// every one of those datasources. Falling back to <c>/api/frontend/settings</c> reads the same list
    /// through the route Grafana's own UI uses, which any signed-in user may call.
    /// <para>
    /// The fallback is reached on 403 only. A 401, a redirect or a 5xx means the credential or the endpoint
    /// is wrong, and retrying a second route would just replace one clear diagnosis with two vague ones.
    /// </para>
    /// </remarks>
    public async Task<DatasourceIndex> ListDatasourcesAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("api/datasources", ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        if (response.IsSuccessStatusCode)
        {
            var array = ParseArray(content);
            return array is null ? DatasourceIndex.Empty : DatasourceIndex.FromApiResponse(array);
        }

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            _logger.LogError("Failed to list Grafana datasources. {Failure}", DescribeFailure(response, content));
            return DatasourceIndex.Empty;
        }

        _logger.LogInformation(
            "This credential may not list datasources directly (403 on /api/datasources — that route is " +
            "admin-only). Reading the datasource list from /api/frontend/settings instead.");

        return await ListDatasourcesFromFrontendSettingsAsync(ct);
    }

    private async Task<DatasourceIndex> ListDatasourcesFromFrontendSettingsAsync(CancellationToken ct)
    {
        var response = await _httpClient.GetAsync("api/frontend/settings", ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to list Grafana datasources. {Failure}", DescribeFailure(response, content));
            return DatasourceIndex.Empty;
        }

        var settings = ParseObject(content);
        if (settings is null)
        {
            _logger.LogError(
                "Grafana returned a body that is not a JSON object for /api/frontend/settings. Response: {Response}",
                Truncate(content));
            return DatasourceIndex.Empty;
        }

        return DatasourceIndex.FromFrontendSettings(settings);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static StringContent JsonContent(JObject payload) =>
        new(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");

    /// <summary>Both the delta and the absolute-date forms of the header are accepted.</summary>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null) return null;

        if (retryAfter.Delta is { } delta) return delta;
        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private JArray? ParseArray(string content)
    {
        try
        {
            return JArray.Parse(content);
        }
        catch (JsonException ex)
        {
            _logger.LogError("Grafana returned a body that is not a JSON array: {Error}. Response: {Response}",
                ex.Message, Truncate(content));
            return null;
        }
    }

    private static JObject? ParseObject(string content)
    {
        try
        {
            return JObject.Parse(content);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string content) =>
        content.Length <= 500 ? content : content[..500] + "…";

    /// <summary>
    /// Renders a failed response for the operator, translating the gateway's login redirect into the auth
    /// problem it actually is.
    /// </summary>
    /// <remarks>
    /// A 3xx on an API path is never a real redirect for this client: it means the request was not
    /// recognised as an authenticated API call and is being sent to a sign-in page. Saying so is the
    /// difference between "check the endpoint and API key" and an unexplained parse error.
    /// </remarks>
    private static string DescribeFailure(HttpResponseMessage response, string content)
    {
        var status = $"{(int)response.StatusCode} {response.StatusCode}";

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var location = response.Headers.Location?.OriginalString ?? "(no Location header)";
            return $"{status} redirect to '{location}' — the request was not accepted as an authenticated " +
                   "API call. This endpoint expects a Grafana API token; a Coralogix API key is rejected here.";
        }

        var message = ParseObject(content)?.Value<string>("message");
        if (!string.IsNullOrWhiteSpace(message)) return $"{status}: {message}";

        // Grafana answers some denials with an empty body; "403 Forbidden:" trailing nothing reads like
        // truncated output rather than the whole story.
        return string.IsNullOrWhiteSpace(content) ? $"{status} (empty response body)" : $"{status}: {Truncate(content)}";
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
