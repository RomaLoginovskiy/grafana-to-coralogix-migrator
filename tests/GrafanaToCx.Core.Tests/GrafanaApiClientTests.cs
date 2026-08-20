using System.Net;
using System.Text;
using GrafanaToCx.Core.ApiClient;
using GrafanaToCx.Core.Migration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

public class GrafanaApiClientTests
{
    // ── Publishing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_Success_ReturnsTheUidGrafanaAssigned()
    {
        using var harness = Harness.Responding(HttpStatusCode.OK, """{"uid":"abc123","status":"success"}""");

        var result = await harness.Client.PublishAsync(Request());

        Assert.True(result.Success);
        Assert.Equal("abc123", result.TargetId);
    }

    /// <summary>
    /// With overwrite:false Grafana rejects any save whose version differs from the stored one, and the
    /// transform deliberately omits version — so every update to an existing dashboard would 412.
    /// </summary>
    [Fact]
    public async Task PublishAsync_AlwaysSendsOverwrite()
    {
        using var harness = Harness.Responding(HttpStatusCode.OK, """{"uid":"abc123"}""");

        await harness.Client.PublishAsync(Request());

        Assert.True(harness.Handler.LastRequestBody!.Value<bool>("overwrite"));
    }

    [Fact]
    public async Task PublishAsync_WithFolder_SendsFolderUidNeverFolderId()
    {
        using var harness = Harness.Responding(HttpStatusCode.OK, """{"uid":"abc123"}""");

        await harness.Client.PublishAsync(Request(folderId: "folder-uid-1"));

        var body = harness.Handler.LastRequestBody!;
        Assert.Equal("folder-uid-1", body.Value<string>("folderUid"));
        Assert.Null(body["folderId"]);
    }

    /// <summary>An absent folderUid means the General folder; an empty string is rejected.</summary>
    [Fact]
    public async Task PublishAsync_WithoutFolder_OmitsFolderUidEntirely()
    {
        using var harness = Harness.Responding(HttpStatusCode.OK, """{"uid":"abc123"}""");

        await harness.Client.PublishAsync(Request(folderId: null));

        Assert.Null(harness.Handler.LastRequestBody!["folderUid"]);
    }

    [Fact]
    public async Task PublishAsync_SendsTheConfiguredCommitMessage()
    {
        using var harness = Harness.Responding(HttpStatusCode.OK, """{"uid":"abc123"}""",
            new GrafanaPublishOptions { Message = "migrated by test" });

        await harness.Client.PublishAsync(Request());

        Assert.Equal("migrated by test", harness.Handler.LastRequestBody!.Value<string>("message"));
    }

    // ── Failure classification ────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_ErrorResponse_SurfacesGrafanaMessageAndStaysRetryable()
    {
        using var harness = Harness.Responding(
            HttpStatusCode.PreconditionFailed, """{"message":"the dashboard has been changed by someone else"}""");

        var result = await harness.Client.PublishAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(HttpStatusCode.PreconditionFailed, result.StatusCode);
        Assert.Contains("changed by someone else", result.ErrorMessage);
        Assert.Equal(FailureKind.Retryable, RetryPolicy.Classify(result.StatusCode!.Value));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task PublishAsync_DeterministicFailure_IsClassifiedCritical(HttpStatusCode status)
    {
        using var harness = Harness.Responding(status, """{"message":"nope"}""");

        var result = await harness.Client.PublishAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(FailureKind.Critical, RetryPolicy.Classify(result.StatusCode!.Value));
    }

    [Fact]
    public async Task PublishAsync_TooManyRequests_ReportsTheServerStatedWait()
    {
        using var harness = Harness.Responding(HttpStatusCode.TooManyRequests, """{"message":"slow down"}""",
            configureResponse: r => r.Headers.Add("Retry-After", "42"));

        var result = await harness.Client.PublishAsync(Request());

        Assert.Equal(TimeSpan.FromSeconds(42), result.RetryAfter);
        Assert.Equal(FailureKind.Retryable, RetryPolicy.Classify(result.StatusCode!.Value));
    }

    /// <summary>
    /// A non-2xx body is not a dashboard response. Parsing it as one is how the prior art turns an auth
    /// failure into a NullReferenceException.
    /// </summary>
    [Fact]
    public async Task PublishAsync_HtmlErrorPage_FailsCleanlyInsteadOfThrowing()
    {
        using var harness = Harness.Responding(HttpStatusCode.BadGateway, "<html><body>502</body></html>");

        var result = await harness.Client.PublishAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("502", result.ErrorMessage);
    }

    [Fact]
    public async Task PublishAsync_SuccessWithoutUid_IsTreatedAsAFailure()
    {
        using var harness = Harness.Responding(HttpStatusCode.OK, """{"status":"success"}""");

        var result = await harness.Client.PublishAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("no uid", result.ErrorMessage);
    }

    [Fact]
    public async Task PublishAsync_TransportFailure_IsReportedAsANetworkErrorAndStaysRetryable()
    {
        using var harness = Harness.Throwing(new HttpRequestException("connection reset"));

        var result = await harness.Client.PublishAsync(Request());

        Assert.False(result.Success);
        Assert.Null(result.StatusCode);
        Assert.Contains("connection reset", result.ErrorMessage);
    }

    // ── Catalog, folders, datasources ─────────────────────────────────────────

    [Fact]
    public async Task GetCatalogAsync_MapsUidTitleAndFolder()
    {
        using var harness = Harness.Responding(HttpStatusCode.OK, """
        [
          {"uid":"a","title":"Alpha","folderUid":"f1"},
          {"uid":"b","title":"Beta","folderUid":""}
        ]
        """);

        var catalog = await harness.Client.GetCatalogAsync();

        Assert.Equal(2, catalog.Count);
        Assert.Equal(new TargetDashboard("a", "Alpha", "f1"), catalog[0]);
        Assert.Null(catalog[1].FolderId);   // General folder is "no folder" to the plan
    }

    [Fact]
    public async Task GetCatalogAsync_ErrorResponse_ReturnsEmptyWithoutThrowing()
    {
        using var harness = Harness.Responding(HttpStatusCode.Unauthorized, """{"message":"invalid API key"}""");

        Assert.Empty(await harness.Client.GetCatalogAsync());
    }

    /// <summary>The folder uid is what the dashboard save API accepts; the numeric id is not.</summary>
    [Fact]
    public async Task ListFoldersAsync_UsesTheUidNotTheNumericId()
    {
        using var harness = Harness.Responding(HttpStatusCode.OK, """[{"id":7,"uid":"folder-uid","title":"Team"}]""");

        var folders = await harness.Client.ListFoldersAsync();

        Assert.Equal("folder-uid", Assert.Single(folders).Id);
    }

    [Fact]
    public async Task GetOrCreateFolderAsync_ExistingFolder_MatchesCaseInsensitivelyAndDoesNotCreate()
    {
        using var harness = Harness.Responding(HttpStatusCode.OK, """[{"uid":"folder-uid","title":"Team"}]""");

        var id = await harness.Client.GetOrCreateFolderAsync("TEAM");

        Assert.Equal("folder-uid", id);
        Assert.DoesNotContain(harness.Handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task GetOrCreateFolderAsync_MissingFolder_CreatesItAndReturnsTheNewUid()
    {
        using var harness = Harness.RespondingInSequence(
            (HttpStatusCode.OK, "[]"),
            (HttpStatusCode.OK, """{"uid":"new-uid","title":"Team"}"""));

        var id = await harness.Client.GetOrCreateFolderAsync("Team");

        Assert.Equal("new-uid", id);
        Assert.Equal("Team", harness.Handler.LastRequestBody!.Value<string>("title"));
    }

    [Fact]
    public async Task GetOrCreateFolderAsync_CreateRejected_ReturnsNullSoTheCallerStops()
    {
        using var harness = Harness.RespondingInSequence(
            (HttpStatusCode.OK, "[]"),
            (HttpStatusCode.Forbidden, """{"message":"Access denied"}"""));

        Assert.Null(await harness.Client.GetOrCreateFolderAsync("Team"));
    }

    [Fact]
    public async Task ListDatasourcesAsync_BuildsAnIndexKeyedByUidNameAndType()
    {
        using var harness = Harness.Responding(HttpStatusCode.OK, """
        [
          {"uid":"u1","name":"Logs","type":"elasticsearch","isDefault":false},
          {"uid":"u2","name":"Metrics","type":"prometheus","isDefault":true}
        ]
        """);

        var index = await harness.Client.ListDatasourcesAsync();

        Assert.Equal("u1", index.ByName("logs")!.Uid);
        Assert.Equal("u2", index.ByUid("u2")!.Uid);
        Assert.Equal("u2", index.Default!.Uid);
    }

    /// <summary>
    /// /api/datasources is admin-only, so a Viewer or Editor credential gets 403 there while still being
    /// able to read the same list through the route Grafana's own UI calls.
    /// </summary>
    [Fact]
    public async Task ListDatasourcesAsync_Forbidden_FallsBackToFrontendSettings()
    {
        using var harness = Harness.RespondingInSequence(
            (HttpStatusCode.Forbidden, ""),
            (HttpStatusCode.OK, """
            {
              "datasources": {
                "Logs":        {"uid":"u1","name":"Logs","type":"elasticsearch","isDefault":false},
                "Metrics":     {"uid":"u2","name":"Metrics","type":"prometheus","isDefault":true},
                "-- Grafana --": {"uid":"grafana","name":"-- Grafana --","type":"datasource"},
                "-- Mixed --":   {"uid":"-- Mixed --","name":"-- Mixed --","type":"datasource"}
              }
            }
            """));

        var index = await harness.Client.ListDatasourcesAsync();

        Assert.EndsWith("/api/frontend/settings", harness.Handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("u1", index.ByName("logs")!.Uid);
        Assert.Equal("u2", index.Default!.Uid);

        // Built-ins dropped: all three share type "datasource", so keeping them would make every lookup
        // for that type ambiguous.
        Assert.Equal(2, index.All.Count);
        Assert.Null(index.ByUid("grafana"));
        Assert.Empty(index.ByType("datasource"));
    }

    /// <summary>A wrong credential or endpoint must not be retried against a second route.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ListDatasourcesAsync_NonForbiddenFailure_DoesNotFallBack(HttpStatusCode status)
    {
        using var harness = Harness.Responding(status, """{"message":"nope"}""");

        var index = await harness.Client.ListDatasourcesAsync();

        Assert.Empty(index.All);
        Assert.Single(harness.Handler.Requests);
    }

    /// <summary>
    /// The gateway answers an unaccepted call with a redirect to its login page rather than a 401. Left
    /// unfollowed it is a reportable failure; followed, it becomes a 200 whose body is HTML.
    /// </summary>
    [Fact]
    public async Task PublishAsync_LoginRedirect_IsReportedAsAFailure()
    {
        using var harness = Harness.Responding(HttpStatusCode.Found, "",
            configureResponse: r => r.Headers.Location = new Uri("/#/login", UriKind.Relative));

        var result = await harness.Client.PublishAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(HttpStatusCode.Found, result.StatusCode);
        Assert.Contains("/#/login", result.ErrorMessage);
        Assert.Contains("not accepted as an authenticated API call", result.ErrorMessage);
    }

    [Fact]
    public async Task Requests_CarryTheBearerToken()
    {
        using var harness = Harness.Responding(HttpStatusCode.OK, "[]");

        await harness.Client.GetCatalogAsync();

        var authorization = harness.Handler.Requests[0].Headers.Authorization;
        Assert.Equal("Bearer", authorization!.Scheme);
        Assert.Equal("cxup_test_key", authorization.Parameter);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static PublishRequest Request(string? folderId = "folder-uid-1") =>
        new(new JObject { ["uid"] = "dash-uid", ["title"] = "Alpha" },
            "Alpha", "dash-uid", folderId, ExistingTargetId: null, IsLocked: false);

    private sealed class Harness : IDisposable
    {
        private Harness(StubHandler handler, GrafanaPublishOptions options)
        {
            Handler = handler;
            Client = new GrafanaApiClient(
                NullLogger<GrafanaApiClient>.Instance,
                "https://api.coralogix.com/grafana",
                "cxup_test_key",
                options,
                handler);
        }

        public StubHandler Handler { get; }
        public GrafanaApiClient Client { get; }

        public static Harness Responding(
            HttpStatusCode status,
            string body,
            GrafanaPublishOptions? options = null,
            Action<HttpResponseMessage>? configureResponse = null) =>
            new(new StubHandler([(status, body)], repeatLast: true, configureResponse), options ?? new());

        public static Harness RespondingInSequence(params (HttpStatusCode Status, string Body)[] responses) =>
            new(new StubHandler(responses, repeatLast: false, null), new GrafanaPublishOptions());

        public static Harness Throwing(Exception exception) =>
            new(new StubHandler([], repeatLast: false, null) { Throw = exception }, new GrafanaPublishOptions());

        public void Dispose()
        {
            Client.Dispose();
            Handler.Dispose();
        }
    }

    private sealed class StubHandler(
        IReadOnlyList<(HttpStatusCode Status, string Body)> responses,
        bool repeatLast,
        Action<HttpResponseMessage>? configureResponse) : HttpMessageHandler
    {
        private int _index;

        public List<HttpRequestMessage> Requests { get; } = [];
        public JObject? LastRequestBody { get; private set; }
        public Exception? Throw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                LastRequestBody = JObject.Parse(body);
            }

            if (Throw is not null) throw Throw;

            var (status, content) = _index < responses.Count
                ? responses[_index]
                : repeatLast ? responses[^1] : (HttpStatusCode.OK, "[]");

            _index++;

            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };

            configureResponse?.Invoke(response);
            return response;
        }
    }
}
