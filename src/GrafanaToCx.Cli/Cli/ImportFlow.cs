using GrafanaToCx.Core.ApiClient;
using GrafanaToCx.Core.Migration;
using Sharprompt;

namespace GrafanaToCx.Cli.Cli;

/// <summary>
/// A source dashboard file discovered under the import root, with the identity read from its JSON.
/// </summary>
public sealed record ImportSourceFile(string AbsolutePath, string RelativePath, string? Uid, string Title);

/// <summary>How source files are mapped onto destination folders.</summary>
public enum GroupingStrategy
{
    /// <summary>Split each filename on the configured separator and take the leading segments.</summary>
    FilenamePrefix,

    /// <summary>One destination folder per source subdirectory.</summary>
    Subdirectories,

    /// <summary>Everything into one folder.</summary>
    SingleFolder
}

/// <summary>
/// Builds an <see cref="ImportPlan"/> from a directory of Grafana dashboard exports: enumerate, group into
/// folder names, let the user review and adjust, then resolve the destination folders.
/// </summary>
/// <remarks>
/// Nothing here touches the target API until the user accepts a grouping, so separator and
/// segment-count changes are instant and free. All grouping rules live in
/// <see cref="FilenameFolderGrouper"/> so they can be unit-tested without a console.
/// The destination is reached only through <see cref="IDashboardFolderTarget"/>, so the same grouping
/// UX serves both Coralogix custom dashboards and Coralogix-hosted Grafana.
/// </remarks>
public sealed class ImportFlow(IDashboardFolderTarget folderTarget)
{
    private enum NameSource { JsonTitle, FilenameRemainder }

    private const string UngroupedLabel = "(no folder)";

    /// <summary>
    /// Enumerates dashboard JSON under <paramref name="rootDir"/> and reads uid/title from each file.
    /// </summary>
    public static IReadOnlyList<ImportSourceFile> Discover(string rootDir, bool recursive)
    {
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        return Directory.GetFiles(rootDir, "*.json", searchOption)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(rootDir, path).Replace('\\', '/');
                var fallbackTitle = Path.GetFileNameWithoutExtension(path);

                string? uid = null;
                var title = fallbackTitle;
                try
                {
                    (uid, title) = ImportSourceProbe.ReadIdentity(File.ReadAllText(path), fallbackTitle);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"  Warning: could not read '{relative}' ({ex.Message}).");
                }

                return new ImportSourceFile(path, relative, uid, title);
            })
            .ToList();
    }

    /// <summary>
    /// Builds a plan without prompting, using the configured grouping defaults.
    /// </summary>
    public async Task<ImportPlan?> BuildPlanAsync(
        string rootDir,
        IReadOnlyList<ImportSourceFile> files,
        FolderGroupingSettings grouping,
        GroupingStrategy strategy = GroupingStrategy.FilenamePrefix,
        CancellationToken ct = default)
    {
        var groups = GroupBy(strategy, rootDir, files, grouping, singleFolderName: null);
        var folderIds = await ResolveFoldersAsync(groups, parentFolderId: null, ct);
        return folderIds is null ? null : BuildPlan(rootDir, groups, folderIds, files, NameSource.JsonTitle);
    }

    /// <summary>
    /// Picks the grouping that matches how the source is actually laid out.
    /// </summary>
    /// <remarks>
    /// A directory tree already encodes the intended folders — one per team, typically — so mirroring it
    /// beats re-deriving folders from filenames that were never written with a separator in mind. A flat
    /// directory carries no such structure, leaving the filename as the only signal.
    /// </remarks>
    public static GroupingStrategy ChooseStrategy(IReadOnlyList<ImportSourceFile> files) =>
        files.Any(f => f.RelativePath.Contains('/'))
            ? GroupingStrategy.Subdirectories
            : GroupingStrategy.FilenamePrefix;

    /// <summary>
    /// Walks the user through grouping, folder mapping and overwrite selection.
    /// Returns null when the user cancels.
    /// </summary>
    public async Task<InteractiveImportSelection?> BuildPlanInteractiveAsync(
        string rootDir,
        IReadOnlyList<ImportSourceFile> files,
        ImportSettings settings,
        CancellationToken ct = default)
    {
        var separator = settings.Grouping.Separator;
        var segmentCount = settings.Grouping.SegmentCount;
        var segmentStart = settings.Grouping.SegmentStart;
        var strategy = ChooseStrategy(files);
        var nameSource = NameSource.JsonTitle;
        var singleFolderName = Path.GetFileName(rootDir.TrimEnd(Path.DirectorySeparatorChar, '/'));
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var grouping = new FolderGroupingSettings
            {
                Separator = separator,
                SegmentCount = segmentCount,
                SegmentStart = segmentStart,
                Recursive = settings.Grouping.Recursive,
                UngroupedFolderName = settings.Grouping.UngroupedFolderName
            };

            var groups = ApplyRenames(GroupBy(strategy, rootDir, files, grouping, singleFolderName), renames);

            PrintPreview(rootDir, groups, strategy, separator, segmentCount, segmentStart, nameSource,
                folderTarget.TargetDisplayName);

            var choice = Prompt.Select(
                "What next?", BuildMenu(strategy, separator, segmentCount, segmentStart, nameSource, files));

            if (choice.StartsWith("Accept", StringComparison.Ordinal))
            {
                // Nesting is skipped rather than offered-and-ignored on flat-folder targets (Grafana 10 OSS
                // without the nestedFolders toggle); matching against existing folders still applies there,
                // so the prompt itself is not skipped.
                var placement = await PromptFolderPlacementAsync(groups, ct);
                if (placement is null or { Cancelled: true }) return null;

                // A mapping onto existing folders is already resolved — re-resolving by name would create
                // the very duplicates that strategy exists to avoid.
                var folderIds = placement.ExplicitMapping is { } mapping
                    ? new Dictionary<string, string?>(mapping, StringComparer.OrdinalIgnoreCase)
                    : await ResolveFoldersAsync(groups, placement.ParentFolderId, ct);
                if (folderIds is null) return null;

                var overwrite = Prompt.Confirm(
                    $"Overwrite dashboards that already exist in {folderTarget.TargetDisplayName}?",
                    defaultValue: settings.OverwriteExisting);
                Console.WriteLine(overwrite
                    ? "  → Existing dashboards will be replaced."
                    : "  → Existing dashboards will be skipped.");

                if (!Prompt.Confirm("Proceed with import?", defaultValue: true))
                {
                    Console.WriteLine("Aborted.");
                    return null;
                }

                return new InteractiveImportSelection(
                    BuildPlan(rootDir, groups, folderIds, files, nameSource), overwrite);
            }

            if (choice.StartsWith("Change separator", StringComparison.Ordinal))
            {
                var input = Prompt.Input<string>("Separator", defaultValue: separator);
                if (string.IsNullOrEmpty(input))
                    Console.Error.WriteLine("Separator cannot be empty — keeping current.");
                else
                    separator = input;
            }
            else if (choice.StartsWith("Change segment count", StringComparison.Ordinal))
            {
                var input = Prompt.Input<int>("Number of segments", defaultValue: segmentCount);
                if (input < 1)
                    Console.Error.WriteLine("Segment count must be at least 1 — keeping current.");
                else
                    segmentCount = input;
            }
            else if (choice.StartsWith("Pick which segment", StringComparison.Ordinal))
            {
                segmentStart = PromptSegmentStart(files, separator, segmentStart);
            }
            else if (choice.StartsWith("Show files", StringComparison.Ordinal))
            {
                ShowFilesPerFolder(groups, nameSource);
            }
            else if (choice.StartsWith("Rename", StringComparison.Ordinal))
            {
                RenameFolder(groups, renames);
            }
            else if (choice.StartsWith("Group by subdirectories", StringComparison.Ordinal))
            {
                strategy = GroupingStrategy.Subdirectories;
            }
            else if (choice.StartsWith("Group by filename prefix", StringComparison.Ordinal))
            {
                strategy = GroupingStrategy.FilenamePrefix;
            }
            else if (choice.StartsWith("Put everything in one folder", StringComparison.Ordinal))
            {
                singleFolderName = Prompt.Input<string>("Folder name", defaultValue: singleFolderName);
                strategy = GroupingStrategy.SingleFolder;
            }
            else if (choice.StartsWith("Dashboard names", StringComparison.Ordinal))
            {
                nameSource = nameSource == NameSource.JsonTitle ? NameSource.FilenameRemainder : NameSource.JsonTitle;
            }
            else if (choice.StartsWith("Cancel", StringComparison.Ordinal))
            {
                Console.WriteLine("Aborted.");
                return null;
            }
        }
    }

    /// <summary>
    /// Lets the user point at a segment of a real filename instead of guessing an index.
    /// </summary>
    /// <remarks>
    /// The sample is the file with the most segments, so every position that any file could offer is
    /// selectable. Returns <paramref name="currentStart"/> unchanged when the sample cannot be split.
    /// </remarks>
    private static int PromptSegmentStart(
        IReadOnlyList<ImportSourceFile> files, string separator, int currentStart)
    {
        if (string.IsNullOrEmpty(separator))
        {
            Console.Error.WriteLine("Separator is empty — set a separator first.");
            return currentStart;
        }

        var sample = files
            .Select(f => Path.GetFileNameWithoutExtension(f.RelativePath))
            .Select(stem => (Stem: stem, Segments: stem.Split(separator, StringSplitOptions.None)))
            .OrderByDescending(x => x.Segments.Length)
            .ThenBy(x => x.Stem, StringComparer.Ordinal)
            .FirstOrDefault();

        if (sample.Segments is not { Length: > 1 })
        {
            Console.Error.WriteLine($"No filename splits on \"{separator}\" — nothing to pick.");
            return currentStart;
        }

        Console.WriteLine();
        Console.WriteLine($"Sample filename: {sample.Stem}");

        var choices = sample.Segments
            .Select((segment, i) => $"{i + 1}: {segment.Trim()}")
            .ToList();

        var selected = Prompt.Select(
            "Folder name starts at which segment?",
            choices,
            defaultValue: choices.ElementAtOrDefault(currentStart - 1));

        return int.Parse(selected[..selected.IndexOf(':')]);
    }

    // ── Grouping ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<ImportFolderGroup> GroupBy(
        GroupingStrategy strategy,
        string rootDir,
        IReadOnlyList<ImportSourceFile> files,
        FolderGroupingSettings grouping,
        string? singleFolderName)
    {
        var relativePaths = files.Select(f => f.RelativePath).ToList();

        // The JSON configuration binder turns a null literal into an empty string, which would otherwise
        // reach the target as a folder named "".
        var ungroupedFolderName = string.IsNullOrWhiteSpace(grouping.UngroupedFolderName)
            ? null
            : grouping.UngroupedFolderName;

        return strategy switch
        {
            GroupingStrategy.FilenamePrefix => FilenameFolderGrouper.Group(
                relativePaths,
                new FolderGroupingOptions(
                    grouping.Separator, grouping.SegmentCount, ungroupedFolderName, grouping.SegmentStart)),

            GroupingStrategy.Subdirectories => GroupBySubdirectory(files, ungroupedFolderName),

            _ => [new ImportFolderGroup(
                singleFolderName,
                files.Select(f => new GroupedImportFile(
                    f.RelativePath,
                    Path.GetFileName(f.RelativePath),
                    singleFolderName,
                    Path.GetFileNameWithoutExtension(f.RelativePath))).ToList(),
                [])]
        };
    }

    private static IReadOnlyList<ImportFolderGroup> GroupBySubdirectory(
        IReadOnlyList<ImportSourceFile> files,
        string? ungroupedFolderName)
    {
        return files
            .GroupBy(f =>
            {
                var slash = f.RelativePath.LastIndexOf('/');
                return slash < 0 ? null : f.RelativePath[..slash];
            }, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key is null)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ImportFolderGroup(
                g.Key ?? ungroupedFolderName,
                g.Select(f => new GroupedImportFile(
                    f.RelativePath,
                    Path.GetFileName(f.RelativePath),
                    g.Key ?? ungroupedFolderName,
                    Path.GetFileNameWithoutExtension(f.RelativePath))).ToList(),
                []))
            .ToList();
    }

    private static IReadOnlyList<ImportFolderGroup> ApplyRenames(
        IReadOnlyList<ImportFolderGroup> groups,
        IReadOnlyDictionary<string, string> renames)
    {
        if (renames.Count == 0) return groups;

        return groups
            .Select(g => g.FolderName is not null && renames.TryGetValue(g.FolderName, out var newName)
                ? g with
                {
                    FolderName = newName,
                    Files = g.Files.Select(f => f with { FolderName = newName }).ToList()
                }
                : g)
            .ToList();
    }

    // ── Preview ───────────────────────────────────────────────────────────────

    private static void PrintPreview(
        string rootDir,
        IReadOnlyList<ImportFolderGroup> groups,
        GroupingStrategy strategy,
        string separator,
        int segmentCount,
        int segmentStart,
        NameSource nameSource,
        string targetDisplayName)
    {
        var total = groups.Sum(g => g.Files.Count);
        var ungrouped = groups.Where(g => g.FolderName is null).Sum(g => g.Files.Count);
        var namedFolders = groups.Count(g => g.FolderName is not null);

        Console.WriteLine();
        Console.WriteLine($"Found {total} dashboard JSON file(s) in '{rootDir}'.");
        Console.WriteLine(strategy switch
        {
            GroupingStrategy.FilenamePrefix => segmentStart <= 1
                ? $"Grouping: split filename on \"{separator}\", first {segmentCount} segment(s) become the folder name."
                : $"Grouping: split filename on \"{separator}\", {segmentCount} segment(s) starting at #{segmentStart} " +
                  "become the folder name; the rest becomes the dashboard name.",
            GroupingStrategy.Subdirectories => $"Grouping: one {targetDisplayName} folder per source subdirectory.",
            _ => $"Grouping: everything into a single {targetDisplayName} folder."
        });
        Console.WriteLine($"Dashboard names from: {(nameSource == NameSource.JsonTitle ? "JSON title" : "filename remainder")}");
        Console.WriteLine();

        var width = groups.Count == 0 ? 0 : groups.Max(g => (g.FolderName ?? UngroupedLabel).Length);

        foreach (var group in groups)
        {
            var label = (group.FolderName ?? UngroupedLabel).PadRight(width);
            Console.WriteLine($"  {label}   {group.Files.Count} dashboard(s)");

            if (group.CaseVariants.Count > 0)
            {
                Console.WriteLine(
                    $"      note: merged case variant(s) {string.Join(", ", group.CaseVariants.Select(v => $"'{v}'"))} " +
                    $"— {targetDisplayName} folder names are case-insensitive.");
            }
        }

        Console.WriteLine(new string('-', Math.Max(width + 24, 32)));
        Console.WriteLine($"  {namedFolders} folder(s), {total} dashboard(s), {ungrouped} ungrouped");

        WarnOnDuplicateNames(groups, nameSource);
        Console.WriteLine();
    }

    /// <summary>
    /// Two dashboards with the same name in one folder collide on the target's name+folder matching, so the
    /// second would replace the first.
    /// </summary>
    private static void WarnOnDuplicateNames(IReadOnlyList<ImportFolderGroup> groups, NameSource nameSource)
    {
        if (nameSource != NameSource.FilenameRemainder) return;

        foreach (var group in groups)
        {
            var duplicates = group.Files
                .GroupBy(f => f.RemainderName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);

            foreach (var duplicate in duplicates)
            {
                Console.WriteLine(
                    $"  Warning: {duplicate.Count()} files in '{group.FolderName ?? UngroupedLabel}' produce the " +
                    $"dashboard name '{duplicate.Key}' — later ones will overwrite earlier ones.");
            }
        }
    }

    private static void ShowFilesPerFolder(IReadOnlyList<ImportFolderGroup> groups, NameSource nameSource)
    {
        var choices = groups.Select(g => g.FolderName ?? UngroupedLabel).ToList();
        var selected = Prompt.Select("Which folder?", choices);
        var group = groups.First(g => (g.FolderName ?? UngroupedLabel) == selected);

        Console.WriteLine();
        Console.WriteLine($"  {selected}");
        foreach (var file in group.Files)
            Console.WriteLine($"    {file.RemainderName}");
        Console.WriteLine();

        if (nameSource == NameSource.JsonTitle)
        {
            Console.WriteLine("  (dashboard names come from each file's JSON title — switch the name source " +
                              "in the menu to use these filename remainders instead)");
            Console.WriteLine();
        }
    }

    private static void RenameFolder(
        IReadOnlyList<ImportFolderGroup> groups,
        Dictionary<string, string> renames)
    {
        var named = groups.Where(g => g.FolderName is not null).Select(g => g.FolderName!).ToList();
        if (named.Count == 0)
        {
            Console.Error.WriteLine("No named folders to rename.");
            return;
        }

        var selected = Prompt.Select("Rename which folder?", named);
        var newName = Prompt.Input<string>("New name", defaultValue: selected);

        if (string.IsNullOrWhiteSpace(newName) || newName == selected) return;

        // Keyed by the derived name so the rename survives separator/segment changes that reproduce it.
        var derived = renames.FirstOrDefault(kv => kv.Value == selected).Key ?? selected;
        renames[derived] = newName;
    }

    private static string[] BuildMenu(
        GroupingStrategy strategy,
        string separator,
        int segmentCount,
        int segmentStart,
        NameSource nameSource,
        IReadOnlyList<ImportSourceFile> files)
    {
        var items = new List<string> { "Accept this grouping" };

        if (strategy == GroupingStrategy.FilenamePrefix)
        {
            items.Add($"Change separator (currently \"{separator}\")");
            items.Add($"Change segment count (currently {segmentCount})");

            // Offered only when some filename actually has a segment after the first, so the picker can
            // never be opened onto a list with nothing to choose.
            if (files.Any(f => Path.GetFileNameWithoutExtension(f.RelativePath)
                    .Split(separator, StringSplitOptions.None).Length > 1))
            {
                items.Add($"Pick which segment starts the folder name (currently #{segmentStart})");
            }
        }

        items.Add("Show files per folder");
        items.Add("Rename a folder");

        if (strategy != GroupingStrategy.FilenamePrefix) items.Add("Group by filename prefix");
        if (strategy != GroupingStrategy.Subdirectories) items.Add("Group by subdirectories");

        items.Add("Put everything in one folder");
        items.Add($"Dashboard names: currently \"{(nameSource == NameSource.JsonTitle ? "JSON title" : "filename remainder")}\"");
        items.Add("Cancel");

        return [.. items];
    }

    // ── Destination folders ───────────────────────────────────────────────────

    /// <param name="ExplicitMapping">
    /// Folder id per group name, already resolved. Set only by the match-existing strategy; null means the
    /// caller should resolve folders itself under <paramref name="ParentFolderId"/>.
    /// </param>
    private sealed record FolderPlacement(
        string? ParentFolderId,
        IReadOnlyDictionary<string, string?>? ExplicitMapping,
        bool Cancelled);

    private async Task<FolderPlacement?> PromptFolderPlacementAsync(
        IReadOnlyList<ImportFolderGroup> groups, CancellationToken ct)
    {
        var matchExisting = $"Put dashboards into matching {folderTarget.TargetDisplayName} folders that already exist";
        var nestUnderParent = $"Nest all under a parent {folderTarget.TargetDisplayName} folder (preserves structure)";
        const string topLevel = "Create each folder at the top level";

        var strategies = new List<string> { matchExisting };

        // Offered only where it can be honoured — on a flat-folder target a parent would be silently ignored.
        if (folderTarget.SupportsNestedFolders) strategies.Add(nestUnderParent);
        strategies.Add(topLevel);

        Console.WriteLine();
        var strategy = Prompt.Select("Folder placement strategy", strategies);

        if (strategy == topLevel)
            return new FolderPlacement(null, null, false);

        if (strategy == matchExisting)
            return await PromptExistingFolderMappingAsync(groups, ct);

        Console.WriteLine($"Fetching {folderTarget.TargetDisplayName} folders...");
        var targetFolders = await folderTarget.ListFoldersAsync(ct);
        var rootFolders = targetFolders.Where(f => f.ParentId is null).ToList();

        var choices = new[] { "+ Create new folder" }.Concat(rootFolders.Select(f => f.Name)).ToList();
        var choice = Prompt.Select($"Select or create parent {folderTarget.TargetDisplayName} folder", choices);

        if (choice != "+ Create new folder")
        {
            var chosen = rootFolders.First(f => f.Name == choice);
            Console.WriteLine($"  → Using existing folder '{chosen.Name}'");
            return new FolderPlacement(chosen.Id, null, false);
        }

        var name = Prompt.Input<string>("New parent folder name", defaultValue: "Imported Dashboards");
        Console.Write($"  Creating parent folder '{name}'... ");
        var parentId = await folderTarget.GetOrCreateFolderAsync(name, null, ct);

        if (parentId is null)
        {
            Console.Error.WriteLine($"Failed to create parent folder '{name}'.");
            return new FolderPlacement(null, null, true);
        }

        Console.WriteLine($"OK (id: {parentId})");
        return new FolderPlacement(parentId, null, false);
    }

    // ── Matching against folders that already exist ───────────────────────────

    private const string CreateChoiceLabel = "+ create new folder with this name";
    private const string NoFolderChoiceLabel = "(no folder)";

    /// <summary>What a single group should map to once the user accepts.</summary>
    private sealed record MappingChoice(string GroupName, TargetFolder? Folder, FolderMatchKind Kind, bool Create)
    {
        public string Describe(Func<TargetFolder, string> pathOf) => Folder is not null
            ? $"{pathOf(Folder)}   ({DescribeKind()})"
            : Create ? CreateChoiceLabel : NoFolderChoiceLabel;

        private string DescribeKind() => Kind switch
        {
            FolderMatchKind.Exact => "exact name match",
            FolderMatchKind.Normalized => "matched ignoring punctuation",
            FolderMatchKind.Contains => "matched on a contained name — check this one",
            _ => "chosen by hand"
        };
    }

    /// <summary>
    /// Suggests an existing destination folder per group, then lets the user correct any of them before
    /// anything is written.
    /// </summary>
    private async Task<FolderPlacement?> PromptExistingFolderMappingAsync(
        IReadOnlyList<ImportFolderGroup> groups, CancellationToken ct)
    {
        Console.WriteLine($"Fetching {folderTarget.TargetDisplayName} folders...");
        var existing = await folderTarget.ListFoldersAsync(ct);

        if (existing.Count == 0)
        {
            Console.WriteLine(
                $"  No folders exist in {folderTarget.TargetDisplayName} yet — every folder will be created.");
            return new FolderPlacement(null, null, false);
        }

        var pathOf = BuildPathResolver(existing);
        var groupNames = groups.Where(g => g.FolderName is not null).Select(g => g.FolderName!).ToList();

        if (groupNames.Count == 0)
            return new FolderPlacement(null, null, false);

        var choices = ExistingFolderMatcher.MatchAll(groupNames, existing)
            .ToDictionary(
                match => match.GroupName,
                match => new MappingChoice(match.GroupName, match.Folder, match.Kind, Create: match.Folder is null),
                StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            PrintMapping(groupNames, choices, pathOf);

            var action = Prompt.Select("What next?", ["Accept this mapping", "Change one mapping", "Cancel"]);

            if (action.StartsWith("Cancel", StringComparison.Ordinal))
            {
                Console.WriteLine("Aborted.");
                return new FolderPlacement(null, null, true);
            }

            if (action.StartsWith("Change", StringComparison.Ordinal))
            {
                ChangeOneMapping(groupNames, choices, existing, pathOf);
                continue;
            }

            var resolved = await MaterializeMappingAsync(groupNames, choices, ct);
            return resolved is null
                ? new FolderPlacement(null, null, true)
                : new FolderPlacement(null, resolved, false);
        }
    }

    private static void PrintMapping(
        IReadOnlyList<string> groupNames,
        IReadOnlyDictionary<string, MappingChoice> choices,
        Func<TargetFolder, string> pathOf)
    {
        var width = groupNames.Max(name => name.Length);

        Console.WriteLine();
        Console.WriteLine("Folder mapping:");
        foreach (var name in groupNames)
            Console.WriteLine($"  {name.PadRight(width)}  →  {choices[name].Describe(pathOf)}");

        var unmatched = groupNames.Count(name => choices[name] is { Folder: null, Create: true });
        if (unmatched > 0)
            Console.WriteLine($"  ({unmatched} group(s) matched nothing and will create a new folder)");

        // Two groups in one folder is legitimate, but the import matches existing dashboards on name +
        // folder, so same-named dashboards from different groups would silently replace each other.
        var collisions = groupNames
            .Where(name => choices[name].Folder is not null)
            .GroupBy(name => choices[name].Folder!.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);

        foreach (var collision in collisions)
        {
            var names = string.Join("', '", collision);
            Console.WriteLine(
                $"  Warning: '{names}' all map to {pathOf(choices[collision.First()].Folder!)} — " +
                "dashboards sharing a name across those groups will overwrite each other.");
        }

        Console.WriteLine();
    }

    private static void ChangeOneMapping(
        IReadOnlyList<string> groupNames,
        Dictionary<string, MappingChoice> choices,
        IReadOnlyList<TargetFolder> existing,
        Func<TargetFolder, string> pathOf)
    {
        var group = Prompt.Select("Change which group?", groupNames);

        // Ordered by path so a long folder list reads like the destination's own tree.
        var folders = existing.OrderBy(pathOf, StringComparer.OrdinalIgnoreCase).ToList();
        var options = new List<string> { CreateChoiceLabel, NoFolderChoiceLabel };
        options.AddRange(folders.Select(pathOf));

        var current = choices[group];
        var preselected = current.Folder is not null ? pathOf(current.Folder)
            : current.Create ? CreateChoiceLabel
            : NoFolderChoiceLabel;

        var picked = Prompt.Select($"'{group}' →", options, defaultValue: preselected);

        choices[group] = picked switch
        {
            CreateChoiceLabel => new MappingChoice(group, null, FolderMatchKind.None, Create: true),
            NoFolderChoiceLabel => new MappingChoice(group, null, FolderMatchKind.None, Create: false),
            _ => new MappingChoice(
                group,
                folders.First(f => pathOf(f) == picked),
                FolderMatchKind.None,
                Create: false)
        };
    }

    /// <summary>
    /// Turns accepted choices into folder ids, creating only the folders the user asked to create.
    /// Returns null when a folder could not be created and the import must not proceed.
    /// </summary>
    private async Task<Dictionary<string, string?>?> MaterializeMappingAsync(
        IReadOnlyList<string> groupNames,
        IReadOnlyDictionary<string, MappingChoice> choices,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var toCreate = groupNames.Where(name => choices[name] is { Folder: null, Create: true }).ToList();

        if (toCreate.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Creating {toCreate.Count} new {folderTarget.TargetDisplayName} folder(s)...");
        }

        foreach (var name in groupNames)
        {
            var choice = choices[name];

            if (choice.Folder is not null)
            {
                result[name] = choice.Folder.Id;
                continue;
            }

            if (!choice.Create)
            {
                result[name] = null;
                continue;
            }

            Console.Write($"  '{name}'... ");
            var created = await folderTarget.GetOrCreateFolderAsync(name, null, ct);

            if (created is null)
            {
                Console.Error.WriteLine($"Failed to create folder '{name}'.");
                return null;
            }

            Console.WriteLine($"OK (id: {created})");
            result[name] = created;
        }

        return result;
    }

    /// <summary>
    /// Renders a folder as its full path ("Team / Sub"), so two folders sharing a leaf name stay
    /// distinguishable in the picker.
    /// </summary>
    private static Func<TargetFolder, string> BuildPathResolver(IReadOnlyList<TargetFolder> folders)
    {
        var byId = folders
            .GroupBy(f => f.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var cache = new Dictionary<string, string>(StringComparer.Ordinal);

        return folder =>
        {
            if (cache.TryGetValue(folder.Id, out var cached)) return cached;

            var segments = new List<string>();
            var current = folder;

            // Bounded by the folder count so a parent cycle in the destination's data cannot hang the CLI.
            for (var depth = 0; depth <= folders.Count && current is not null; depth++)
            {
                segments.Add(current.Name);
                current = current.ParentId is not null && byId.TryGetValue(current.ParentId, out var parent)
                    ? parent
                    : null;
            }

            segments.Reverse();
            var path = string.Join(" / ", segments);
            cache[folder.Id] = path;
            return path;
        };
    }

    private async Task<Dictionary<string, string?>?> ResolveFoldersAsync(
        IReadOnlyList<ImportFolderGroup> groups,
        string? parentFolderId,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var named = groups.Where(g => g.FolderName is not null).ToList();

        if (named.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Resolving {folderTarget.TargetDisplayName} folders...");
        }

        foreach (var group in named)
        {
            Console.Write($"  '{group.FolderName}'... ");
            var folderId = await folderTarget.GetOrCreateFolderAsync(group.FolderName!, parentFolderId, ct);

            if (folderId is null)
            {
                Console.Error.WriteLine($"Failed to resolve folder '{group.FolderName}'.");
                return null;
            }

            Console.WriteLine($"OK (id: {folderId})");
            result[group.FolderName!] = folderId;
        }

        return result;
    }

    private static ImportPlan BuildPlan(
        string rootDir,
        IReadOnlyList<ImportFolderGroup> groups,
        IReadOnlyDictionary<string, string?> folderIds,
        IReadOnlyList<ImportSourceFile> files,
        NameSource nameSource)
    {
        // Identity was already read during discovery — reuse it rather than re-reading files that run
        // to nearly a megabyte each.
        var byPath = files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var items = new List<ImportPlanItem>();

        foreach (var group in groups)
        {
            string? cxFolderId = null;
            if (group.FolderName is not null && folderIds.TryGetValue(group.FolderName, out var resolved))
                cxFolderId = resolved;

            foreach (var file in group.Files)
            {
                if (!byPath.TryGetValue(file.RelativePath, out var source))
                    continue;

                items.Add(new ImportPlanItem(
                    source.AbsolutePath,
                    source.RelativePath,
                    source.Uid,
                    source.Title,
                    cxFolderId,
                    group.FolderName ?? UngroupedLabel,
                    nameSource == NameSource.FilenameRemainder ? file.RemainderName : null));
            }
        }

        return new ImportPlan(rootDir, items);
    }
}

/// <summary>Result of the interactive import prompts.</summary>
public sealed record InteractiveImportSelection(ImportPlan Plan, bool OverwriteExisting);
