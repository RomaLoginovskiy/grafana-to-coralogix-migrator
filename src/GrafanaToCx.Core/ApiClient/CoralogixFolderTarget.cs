namespace GrafanaToCx.Core.ApiClient;

/// <summary>
/// Adapts <see cref="ICoralogixFoldersClient"/> to the target-agnostic folder port.
/// </summary>
public sealed class CoralogixFolderTarget(ICoralogixFoldersClient client) : IDashboardFolderTarget
{
    public string TargetDisplayName => "Coralogix";

    public bool SupportsNestedFolders => true;

    public async Task<IReadOnlyList<TargetFolder>> ListFoldersAsync(CancellationToken ct = default)
    {
        var folders = await client.ListFoldersAsync(ct);
        return folders.Select(f => new TargetFolder(f.Id, f.Name, f.ParentId)).ToList();
    }

    public Task<string?> GetOrCreateFolderAsync(
        string name, string? parentId = null, CancellationToken ct = default) =>
        client.GetOrCreateFolderAsync(name, parentId, ct);
}
