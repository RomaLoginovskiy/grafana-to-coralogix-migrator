using GrafanaToCx.Core.ApiClient;

namespace GrafanaToCx.Core.Migration;

/// <summary>How confidently a derived folder name was matched to a folder that already exists.</summary>
public enum FolderMatchKind
{
    /// <summary>No candidate was close enough; the caller must create a folder or pick one by hand.</summary>
    None,

    /// <summary>Names are equal ignoring case.</summary>
    Exact,

    /// <summary>Names are equal once punctuation, separators and spacing are removed.</summary>
    Normalized,

    /// <summary>One name contains the other, e.g. "DDE - Delivery Data Engineering" and "Delivery Data Engineering".</summary>
    Contains
}

/// <param name="Folder">The matched folder, or null when <paramref name="Kind"/> is <see cref="FolderMatchKind.None"/>.</param>
public sealed record FolderMatch(string GroupName, TargetFolder? Folder, FolderMatchKind Kind);

/// <summary>
/// Matches derived folder names against the folders a destination already has.
/// </summary>
/// <remarks>
/// The destination's own get-or-create only reuses a folder on an exact name match, so a group derived as
/// "DDE - Delivery Data Engineering" creates a second folder alongside an existing "Delivery Data
/// Engineering" instead of importing into it. This finds the folder a person would have picked.
/// <para>
/// Every match is a suggestion the caller is expected to show and let the user override — a wrong match
/// puts dashboards somewhere unexpected, which is tedious to unpick after the fact.
/// </para>
/// </remarks>
public static class ExistingFolderMatcher
{
    /// <summary>
    /// Containment is the loosest rule here, so it is restricted to names long enough to be meaningful.
    /// Without this an existing folder called "YM" would claim every group whose name contains those
    /// letters.
    /// </summary>
    private const int MinimumNormalizedLengthForContains = 4;

    public static IReadOnlyList<FolderMatch> MatchAll(
        IEnumerable<string> groupNames,
        IReadOnlyList<TargetFolder> existingFolders)
    {
        ArgumentNullException.ThrowIfNull(groupNames);
        ArgumentNullException.ThrowIfNull(existingFolders);

        return groupNames.Select(name => Match(name, existingFolders)).ToList();
    }

    public static FolderMatch Match(string groupName, IReadOnlyList<TargetFolder> existingFolders)
    {
        ArgumentNullException.ThrowIfNull(existingFolders);

        if (string.IsNullOrWhiteSpace(groupName) || existingFolders.Count == 0)
            return new FolderMatch(groupName, null, FolderMatchKind.None);

        var exact = Best(existingFolders.Where(f =>
            string.Equals(f.Name, groupName, StringComparison.OrdinalIgnoreCase)));
        if (exact is not null) return new FolderMatch(groupName, exact, FolderMatchKind.Exact);

        var normalizedGroup = Normalize(groupName);
        if (normalizedGroup.Length == 0)
            return new FolderMatch(groupName, null, FolderMatchKind.None);

        var normalized = Best(existingFolders.Where(f => Normalize(f.Name) == normalizedGroup));
        if (normalized is not null) return new FolderMatch(groupName, normalized, FolderMatchKind.Normalized);

        var contains = Best(existingFolders.Where(f =>
        {
            var candidate = Normalize(f.Name);
            return candidate.Length >= MinimumNormalizedLengthForContains &&
                   (normalizedGroup.Contains(candidate, StringComparison.Ordinal) ||
                    candidate.Contains(normalizedGroup, StringComparison.Ordinal));
        }));

        return contains is not null
            ? new FolderMatch(groupName, contains, FolderMatchKind.Contains)
            : new FolderMatch(groupName, null, FolderMatchKind.None);
    }

    /// <summary>
    /// Longest name first, then ordinal by name and id. The longest candidate is the most specific one —
    /// "Delivery Data Engineering" beats "Delivery" for the same group — and the tie-breakers keep the
    /// result stable across runs, since the destination does not promise a folder ordering.
    /// </summary>
    private static TargetFolder? Best(IEnumerable<TargetFolder> candidates) =>
        candidates
            .OrderByDescending(f => Normalize(f.Name).Length)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// Reduces a name to lowercase letters and digits, so separator and punctuation choices stop mattering:
    /// "Wire In / Wire Out" and "wire-in-wire-out" normalise alike.
    /// </summary>
    private static string Normalize(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        return string.Create(name.Length, name, (span, source) =>
        {
            var written = 0;
            foreach (var c in source)
            {
                if (char.IsLetterOrDigit(c))
                    span[written++] = char.ToLowerInvariant(c);
            }

            span[written..].Fill('\0');
        }).TrimEnd('\0');
    }
}
