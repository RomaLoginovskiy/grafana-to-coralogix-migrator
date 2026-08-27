using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Migration;

/// <summary>
/// One source file and the Coralogix destination it has been planned into.
/// </summary>
/// <param name="Uid">Grafana uid from the source JSON, when present. Used for checkpoint identity.</param>
/// <param name="Title">Grafana title. This becomes the Coralogix dashboard name unless overridden.</param>
/// <param name="DashboardNameOverride">
/// Set when the user chose to name dashboards after the filename remainder instead of the JSON title.
/// </param>
public sealed record ImportPlanItem(
    string AbsolutePath,
    string RelativePath,
    string? Uid,
    string Title,
    string? CxFolderId,
    string FolderDisplayName,
    string? DashboardNameOverride = null)
{
    /// <summary>Name the dashboard will be published under.</summary>
    public string EffectiveName =>
        string.IsNullOrWhiteSpace(DashboardNameOverride) ? Title : DashboardNameOverride!;
}

public sealed record ImportPlan(string RootDirectory, IReadOnlyList<ImportPlanItem> Items);

public sealed record ImportRunSummary(int Completed, int Skipped, int Failed)
{
    public int Total => Completed + Skipped + Failed;
}

/// <summary>
/// Reads source dashboard JSON. Exists so <see cref="ImportOrchestrator"/> can be tested without disk.
/// </summary>
public interface IImportSourceReader
{
    Task<string> ReadAsync(string absolutePath, CancellationToken ct = default);
}

public sealed class FileImportSourceReader : IImportSourceReader
{
    public Task<string> ReadAsync(string absolutePath, CancellationToken ct = default) =>
        File.ReadAllTextAsync(absolutePath, ct);
}

/// <summary>
/// Extracts checkpoint/display identity from raw Grafana dashboard JSON.
/// </summary>
public static class ImportSourceProbe
{
    /// <summary>
    /// Reads uid and title, unwrapping the <c>{ "dashboard": … }</c> envelope that the Grafana API returns.
    /// Raw UI exports have these at the top level.
    /// </summary>
    /// <param name="fallbackTitle">Used when the JSON carries no usable title — normally the filename stem.</param>
    public static (string? Uid, string Title) ReadIdentity(string json, string fallbackTitle)
    {
        try
        {
            var root = JObject.Parse(json);
            var dashboard = root["dashboard"] as JObject ?? root;

            var uid = dashboard["uid"]?.ToString();
            var title = dashboard["title"]?.ToString();

            return (
                string.IsNullOrWhiteSpace(uid) ? null : uid,
                string.IsNullOrWhiteSpace(title) ? fallbackTitle : title);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return (null, fallbackTitle);
        }
    }
}
