using System.Text.RegularExpressions;
using GrafanaToCx.Core.GrafanaToGrafana;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Runs the transform over every dashboard export found locally and asserts it changed nothing
/// it was not asked to change.
/// </summary>
/// <remarks>
/// This is the regression net for the whole transform. The prior art's defects — deleting
/// <c>annotations</c> and nested <c>meta</c> at every depth, rewriting <c>templating.list[].query</c> into
/// Lucene syntax, nulling legacy string datasource references, coercing every unmatched datasource to
/// Prometheus — are all invisible to a rule-by-rule unit test on synthetic input, and all show up here
/// as a path outside the allowlist on a realistic corpus.
/// <para>
/// Skips rather than fails when the corpus is absent, so a clean checkout without the sample directories
/// still runs green.
/// </para>
/// </remarks>
public class GrafanaTransformCorpusTests
{
    private static readonly string[] CorpusDirectories =
        ["grafana-backup", "grafana_dashboards", "artifacts/dashboards"];

    /// <summary>
    /// Paths the transform is allowed to change. Anything else differing is collateral damage.
    /// </summary>
    private static readonly Regex[] AllowedChanges =
    [
        new(@"^(id|uid|version|iteration|meta|folderId|folderUid|folderTitle|isFolder|slug|url|title|__inputs|__requires)$"),
        new(@"(^|\.)datasource$"),
        new(@"(^|\.)datasource\."),
        new(@"^templating\.list\[\d+\]\.(current|options)"),
        new(@"\.alert$"),
        new(@"\.alert\.")
    ];

    private static readonly DatasourceIndex TargetDatasources = new(
    [
        new TargetDatasource("cx-logs-uid", "Logs", "elasticsearch", IsDefault: false),
        new TargetDatasource("cx-metrics-uid", "Metrics", "prometheus", IsDefault: true),
        new TargetDatasource("cx-metrics-v2-uid", "Metrics_v2", "prometheus", IsDefault: false)
    ]);

    public static TheoryData<string> CorpusFiles()
    {
        var data = new TheoryData<string>();
        var root = RepositoryRoot();

        if (root is null)
        {
            data.Add(string.Empty);
            return data;
        }

        var files = CorpusDirectories
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.json", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0) data.Add(string.Empty);
        foreach (var file in files) data.Add(file);

        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Transform_RealExport_ChangesOnlyAllowlistedPaths(string relativePath)
    {
        if (relativePath.Length == 0) return;   // corpus not present in this checkout

        var root = RepositoryRoot()!;
        var source = JObject.Parse(File.ReadAllText(Path.Combine(root, relativePath)));
        var body = source["dashboard"] as JObject ?? source;

        var result = new GrafanaDashboardTransform().Transform(source, new GrafanaTransformOptions
        {
            Datasources = TargetDatasources,
            UidSeed = relativePath
        });

        var unexpected = Diff(body, result.Dashboard)
            .Where(path => !AllowedChanges.Any(r => r.IsMatch(path)))
            .Take(10)
            .ToList();

        Assert.True(unexpected.Count == 0,
            $"{relativePath} changed outside the allowlist:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", unexpected));
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Transform_RealExport_IsIdempotent(string relativePath)
    {
        if (relativePath.Length == 0) return;

        var root = RepositoryRoot()!;
        var source = JObject.Parse(File.ReadAllText(Path.Combine(root, relativePath)));
        var transform = new GrafanaDashboardTransform();

        var options = new GrafanaTransformOptions
        {
            Datasources = TargetDatasources,
            UidSeed = relativePath
        };

        var once = transform.Transform(source, options);
        var twice = transform.Transform((JObject)once.Dashboard.DeepClone(), options);

        Assert.Equal(once.Uid, twice.Uid);
        Assert.True(JToken.DeepEquals(once.Dashboard, twice.Dashboard),
            $"{relativePath}: re-transforming the output changed it, so republishing would not be a no-op. " +
            $"First differing path: {Diff(once.Dashboard, twice.Dashboard).FirstOrDefault() ?? "(structure)"}");
    }

    /// <summary>
    /// Every dashboard-level guarantee the save API depends on, checked across the whole corpus at once.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Transform_RealExport_ProducesAPublishableBody(string relativePath)
    {
        if (relativePath.Length == 0) return;

        var root = RepositoryRoot()!;
        var source = JObject.Parse(File.ReadAllText(Path.Combine(root, relativePath)));

        var result = new GrafanaDashboardTransform().Transform(source, new GrafanaTransformOptions
        {
            Datasources = TargetDatasources,
            UidSeed = relativePath
        });

        Assert.False(result.Dashboard.ContainsKey("id"));
        Assert.False(result.Dashboard.ContainsKey("version"));
        Assert.False(result.Dashboard.ContainsKey("meta"));
        Assert.Matches(@"^[A-Za-z0-9\-_]{1,40}$", result.Uid);
        Assert.False(string.IsNullOrWhiteSpace(result.Title));

        // Every remaining datasource reference is either resolvable on the target, or one Grafana resolves
        // itself. The built-in check comes from the transform rather than a second copy here, because a
        // copy drifts: adding __expr__ to the production list is exactly what a local list would have missed.
        var stale = result.Dashboard
            .Descendants()
            .OfType<JProperty>()
            .Where(p => p.Name == "datasource" && p.Value is JObject)
            .Select(p => (JObject)p.Value)
            .Select(o => (Uid: o.Value<string>("uid"), Type: o.Value<string>("type")))
            .Where(r => !string.IsNullOrEmpty(r.Uid))
            .Distinct()
            .Where(r => TargetDatasources.ByUid(r.Uid) is null
                        && !GrafanaDashboardTransform.IsSelfResolvingDatasource(r.Uid, r.Type))
            .ToList();

        // Unresolved references are deliberately left in place, but every one must be reported so the
        // operator learns about it from the run report rather than from a blank panel.
        if (stale.Count > 0)
        {
            Assert.Contains(result.Diagnostics,
                d => d.Code is TransformCodes.DatasourceUnresolved or TransformCodes.DatasourceAmbiguous);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? _repositoryRoot;
    private static bool _searched;

    private static string? RepositoryRoot()
    {
        if (_searched) return _repositoryRoot;
        _searched = true;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GrafanaToCx.sln")))
            {
                _repositoryRoot = dir.FullName;
                return _repositoryRoot;
            }
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>JSON paths whose value differs between the two documents, or that exist in only one.</summary>
    private static IEnumerable<string> Diff(JToken left, JToken right, string path = "")
    {
        if (left.Type != right.Type)
        {
            yield return path;
            yield break;
        }

        switch (left)
        {
            case JObject leftObject:
            {
                var rightObject = (JObject)right;
                var names = leftObject.Properties().Select(p => p.Name)
                    .Union(rightObject.Properties().Select(p => p.Name), StringComparer.Ordinal);

                foreach (var name in names)
                {
                    var childPath = string.IsNullOrEmpty(path) ? name : $"{path}.{name}";
                    var leftChild = leftObject[name];
                    var rightChild = rightObject[name];

                    if (leftChild is null || rightChild is null)
                    {
                        yield return childPath;
                        continue;
                    }

                    foreach (var difference in Diff(leftChild, rightChild, childPath))
                        yield return difference;
                }
                break;
            }

            case JArray leftArray:
            {
                var rightArray = (JArray)right;
                if (leftArray.Count != rightArray.Count)
                {
                    yield return path;
                    break;
                }

                for (var i = 0; i < leftArray.Count; i++)
                    foreach (var difference in Diff(leftArray[i], rightArray[i], $"{path}[{i}]"))
                        yield return difference;
                break;
            }

            default:
                if (!JToken.DeepEquals(left, right)) yield return path;
                break;
        }
    }
}
