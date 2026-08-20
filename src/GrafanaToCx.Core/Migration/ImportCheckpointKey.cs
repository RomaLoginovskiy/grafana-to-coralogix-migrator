namespace GrafanaToCx.Core.Migration;

/// <summary>
/// A source file and the target folder it is planned to land in, for checkpoint-key resolution.
/// </summary>
public sealed record ImportKeyCandidate(string RelativePath, string? Uid, string? CxFolderId);

/// <summary>
/// Builds checkpoint keys for the import flow.
/// </summary>
/// <remarks>
/// <para>
/// A key is <c>{folder}::{identity}</c>. The folder component is essential: without it, importing the
/// same source directory into a second Coralogix folder would overwrite the first run's entries, so the
/// <see cref="CheckpointEntry.CxDashboardId"/> earned in folder A would be used as the replace target for
/// folder B — silently overwriting A's dashboard while B never gets its own.
/// </para>
/// <para>
/// Identity prefers the Grafana <c>uid</c> so a file rename does not orphan its entry. It falls back to the
/// relative path when the uid is missing or is shared by another file targeting the same folder.
/// </para>
/// </remarks>
public static class ImportCheckpointKey
{
    /// <summary>Folder component used when a dashboard is imported without a folder.</summary>
    public const string NoFolder = "(none)";

    public static string Compose(string? cxFolderId, string identity) =>
        $"{FolderComponent(cxFolderId)}::{identity}";

    public static string FolderComponent(string? cxFolderId) =>
        string.IsNullOrWhiteSpace(cxFolderId) ? NoFolder : cxFolderId;

    public static string UidIdentity(string uid) => $"uid:{uid}";

    public static string PathIdentity(string relativePath) => $"path:{NormalizePath(relativePath)}";

    /// <summary>
    /// Resolves a checkpoint key for every candidate, keyed by normalized relative path.
    /// </summary>
    /// <remarks>
    /// Duplicate uids are resolved over the whole candidate set rather than per file. When two files
    /// targeting the same folder share a uid, <em>both</em> are demoted to path identity — demoting only
    /// the second would make identity depend on enumeration order, so the same file could change key
    /// between runs.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ResolveKeys(IReadOnlyList<ImportKeyCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var normalized = candidates
            .Select(c => c with { RelativePath = NormalizePath(c.RelativePath) })
            .ToList();

        var contestedUids = normalized
            .Where(c => !string.IsNullOrWhiteSpace(c.Uid))
            .GroupBy(c => (Folder: FolderComponent(c.CxFolderId), Uid: c.Uid!), TupleComparer.Instance)
            .Where(g => g.Select(c => c.RelativePath).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(TupleComparer.Instance);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var candidate in normalized)
        {
            var folder = FolderComponent(candidate.CxFolderId);

            var usableUid = !string.IsNullOrWhiteSpace(candidate.Uid) &&
                            !contestedUids.Contains((folder, candidate.Uid!));

            var identity = usableUid
                ? UidIdentity(candidate.Uid!)
                : PathIdentity(candidate.RelativePath);

            result[candidate.RelativePath] = Compose(candidate.CxFolderId, identity);
        }

        return result;
    }

    public static string NormalizePath(string relativePath) =>
        (relativePath ?? string.Empty).Replace('\\', '/').Trim();

    private sealed class TupleComparer : IEqualityComparer<(string Folder, string Uid)>
    {
        public static readonly TupleComparer Instance = new();

        public bool Equals((string Folder, string Uid) x, (string Folder, string Uid) y) =>
            string.Equals(x.Folder, y.Folder, StringComparison.Ordinal) &&
            string.Equals(x.Uid, y.Uid, StringComparison.Ordinal);

        public int GetHashCode((string Folder, string Uid) obj) =>
            HashCode.Combine(obj.Folder, obj.Uid);
    }
}
