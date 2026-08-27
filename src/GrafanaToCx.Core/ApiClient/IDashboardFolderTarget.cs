namespace GrafanaToCx.Core.ApiClient;

/// <summary>
/// The folder operations the import flow needs, independent of whether dashboards are destined for
/// Coralogix custom dashboards or a Coralogix-hosted Grafana.
/// </summary>
/// <remarks>
/// Exists so the grouping/preview/rename UX in <c>ImportFlow</c> is written once. The two targets
/// differ in exactly two observable ways — what they are called, and whether folders can nest — so
/// both are surfaced here rather than being probed by the caller.
/// </remarks>
public interface IDashboardFolderTarget
{
    /// <summary>Shown in prompts and previews, e.g. "Coralogix" or "Grafana".</summary>
    string TargetDisplayName { get; }

    /// <summary>
    /// False for Grafana 10 OSS without the <c>nestedFolders</c> feature toggle, where every folder is
    /// top-level. Callers must skip the parent-folder prompt rather than offer it and discard the answer.
    /// </summary>
    bool SupportsNestedFolders { get; }

    Task<IReadOnlyList<TargetFolder>> ListFoldersAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the id of the folder named <paramref name="name"/>, creating it when absent.
    /// Null means the folder could not be resolved and the caller must not proceed.
    /// </summary>
    Task<string?> GetOrCreateFolderAsync(string name, string? parentId = null, CancellationToken ct = default);
}

/// <param name="Id">
/// Whatever the target's dashboard-save call accepts as a folder reference — a Coralogix folder id,
/// or a Grafana folder <c>uid</c> (never Grafana's numeric <c>id</c>).
/// </param>
public sealed record TargetFolder(string Id, string Name, string? ParentId = null);
