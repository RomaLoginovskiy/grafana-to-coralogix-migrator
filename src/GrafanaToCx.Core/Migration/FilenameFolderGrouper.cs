namespace GrafanaToCx.Core.Migration;

/// <summary>
/// Controls how a source filename is split into a Coralogix folder name and a remainder.
/// </summary>
/// <param name="Separator">Literal separator. Split is ordinal — never treated as a regex.</param>
/// <param name="SegmentCount">Number of consecutive segments that form the folder name.</param>
/// <param name="UngroupedFolderName">
/// Folder for files yielding no prefix. Null means "no folder". Blank is normalised to null: the JSON
/// configuration binder turns a <c>null</c> literal into an empty string, and an empty folder name would
/// otherwise be sent to the target as a real folder to create.
/// </param>
/// <param name="SegmentStart">
/// 1-based index of the first segment forming the folder name. Defaults to 1 — the leading segments.
/// Segments before the window are not discarded; they stay in <see cref="GroupedImportFile.RemainderName"/>,
/// so no part of the source filename is lost when the folder is taken from the middle of the name.
/// </param>
public sealed record FolderGroupingOptions(
    string Separator = " - ",
    int SegmentCount = 2,
    string? UngroupedFolderName = null,
    int SegmentStart = 1)
{
    public string? UngroupedFolderName { get; init; } =
        string.IsNullOrWhiteSpace(UngroupedFolderName) ? null : UngroupedFolderName;
}

/// <param name="RelativePath">Path relative to the import root, '/'-separated. Unique per file.</param>
/// <param name="FolderName">Derived folder, or null when the file could not be grouped.</param>
/// <param name="RemainderName">Filename stem minus the consumed prefix segments.</param>
public sealed record GroupedImportFile(
    string RelativePath,
    string FileName,
    string? FolderName,
    string RemainderName);

/// <param name="CaseVariants">
/// Alternative spellings merged into this group. Non-empty only when source filenames disagreed on casing.
/// </param>
public sealed record ImportFolderGroup(
    string? FolderName,
    IReadOnlyList<GroupedImportFile> Files,
    IReadOnlyList<string> CaseVariants);

/// <summary>
/// Derives Coralogix folder names from the leading segments of dashboard filenames.
/// </summary>
/// <remarks>
/// Pure and I/O-free by design — it takes relative paths rather than scanning the filesystem, so the
/// grouping rules are unit-testable without touching disk. Callers enumerate.
/// </remarks>
public static class FilenameFolderGrouper
{
    public static IReadOnlyList<ImportFolderGroup> Group(
        IReadOnlyList<string> relativeFilePaths,
        FolderGroupingOptions options)
    {
        ArgumentNullException.ThrowIfNull(relativeFilePaths);
        ArgumentNullException.ThrowIfNull(options);

        var grouped = relativeFilePaths
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Derive(path, options))
            .ToList();

        // Folder names that differ only by case resolve to a single Coralogix folder anyway —
        // CoralogixFoldersClient.GetOrCreateFolderAsync matches OrdinalIgnoreCase — so presenting them
        // as separate groups would misrepresent what the import will actually do.
        var buckets = new Dictionary<string, FolderBucket>(StringComparer.OrdinalIgnoreCase);
        var ungrouped = new List<GroupedImportFile>();

        foreach (var file in grouped)
        {
            if (file.FolderName is null)
            {
                ungrouped.Add(file);
                continue;
            }

            if (!buckets.TryGetValue(file.FolderName, out var bucket))
            {
                // Files are ordered by path, so the display spelling is deterministic.
                bucket = new FolderBucket(file.FolderName);
                buckets[file.FolderName] = bucket;
            }

            bucket.Add(file);
        }

        var result = buckets.Values
            .OrderBy(bucket => bucket.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(bucket => bucket.ToGroup())
            .ToList();

        if (ungrouped.Count > 0)
        {
            result.Add(new ImportFolderGroup(
                options.UngroupedFolderName,
                ungrouped,
                []));
        }

        return result;
    }

    private static GroupedImportFile Derive(string relativePath, FolderGroupingOptions options)
    {
        var fileName = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        var stem = StripJsonExtension(fileName);

        if (options.SegmentCount <= 0 || options.SegmentStart <= 0 || string.IsNullOrEmpty(options.Separator))
            return new GroupedImportFile(relativePath, fileName, null, stem);

        var segments = stem.Split(options.Separator, StringSplitOptions.None);
        var skip = options.SegmentStart - 1;

        // Consuming every segment would leave an empty dashboard label, and a window that runs past the
        // end of the name would name a folder after a single dashboard. Neither is what the user means.
        if (segments.Length <= skip + options.SegmentCount)
            return new GroupedImportFile(relativePath, fileName, null, stem);

        var window = segments.Skip(skip).Take(options.SegmentCount).Select(s => s.Trim()).ToList();
        if (window.Any(string.IsNullOrEmpty))
            return new GroupedImportFile(relativePath, fileName, null, stem);

        var folderName = string.Join(options.Separator, window);

        // The remainder keeps its own separators — "Ledger - Recon Monitoring" is one dashboard label,
        // not two more levels of nesting. Segments ahead of the window stay in front of it, so picking a
        // middle segment as the folder never silently drops the ones before it.
        var remainder = string.Join(
            options.Separator,
            segments.Take(skip).Concat(segments.Skip(skip + options.SegmentCount)).Select(s => s.Trim())
                .Where(s => s.Length > 0)).Trim();

        if (remainder.Length == 0)
            return new GroupedImportFile(relativePath, fileName, null, stem);

        return new GroupedImportFile(relativePath, fileName, folderName, remainder);
    }

    private static string StripJsonExtension(string fileName) =>
        fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^5]
            : fileName;

    private static string NormalizePath(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim();

    private sealed class FolderBucket(string displayName)
    {
        private readonly List<GroupedImportFile> _files = [];
        private readonly List<string> _variants = [];

        public string DisplayName { get; } = displayName;

        public void Add(GroupedImportFile file)
        {
            if (!string.Equals(file.FolderName, DisplayName, StringComparison.Ordinal) &&
                !_variants.Contains(file.FolderName!, StringComparer.Ordinal))
            {
                _variants.Add(file.FolderName!);
            }

            _files.Add(file with { FolderName = DisplayName });
        }

        public ImportFolderGroup ToGroup() => new(DisplayName, _files, _variants);
    }
}
