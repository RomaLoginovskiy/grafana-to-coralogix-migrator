namespace GrafanaToCx.Core.ApiClient;

/// <summary>
/// Resolves folders for a dry run without creating any.
/// </summary>
/// <remarks>
/// Necessary rather than convenient: plan construction calls <see cref="GetOrCreateFolderAsync"/>,
/// so a dry run that short-circuits after the plan is built has already created folders on the target.
/// Missing folders get a synthetic id prefixed with <see cref="PendingPrefix"/> so the plan is still
/// complete and printable, and so anything that leaks a synthetic id into a real request is obvious.
/// </remarks>
public sealed class DryRunFolderTarget(IDashboardFolderTarget inner) : IDashboardFolderTarget
{
    public const string PendingPrefix = "(would create) ";

    public string TargetDisplayName => inner.TargetDisplayName;

    public bool SupportsNestedFolders => inner.SupportsNestedFolders;

    public Task<IReadOnlyList<TargetFolder>> ListFoldersAsync(CancellationToken ct = default) =>
        inner.ListFoldersAsync(ct);

    public async Task<string?> GetOrCreateFolderAsync(
        string name, string? parentId = null, CancellationToken ct = default)
    {
        var existing = (await inner.ListFoldersAsync(ct))
            .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        return existing?.Id ?? PendingPrefix + name;
    }

    /// <summary>True when <paramref name="folderId"/> is a placeholder for a folder that does not exist yet.</summary>
    public static bool IsPending(string? folderId) =>
        folderId is not null && folderId.StartsWith(PendingPrefix, StringComparison.Ordinal);
}
