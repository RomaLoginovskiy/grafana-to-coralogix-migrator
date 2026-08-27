using System.Text.RegularExpressions;
using GrafanaToCx.Core.ApiClient;
using GrafanaToCx.Core.Assessment;
using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Converter.Transformations;
using GrafanaToCx.Core.Migration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharprompt;

namespace GrafanaToCx.Cli.Cli;

/// <summary>
/// Command handlers for convert, push, import, migrate, verify, and interactive flows.
/// Preserves existing business logic; uses Sharprompt for interactive prompts.
/// </summary>
public sealed class CommandHandlers
{
    private readonly ILoggerFactory _loggerFactory;

    public CommandHandlers(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    private IGrafanaToCxConverter CreateConverter(MultiLuceneMergeOptions? mergeOptions = null)
    {
        var converterLogger = _loggerFactory.CreateLogger<GrafanaToCxConverter>();
        return new GrafanaToCxConverter(converterLogger, mergeOptions);
    }

    // ── Convert ───────────────────────────────────────────────────────────────

    public async Task<int> RunConvertAsync(string input, string? output)
    {
        var converter = CreateConverter();

        if (Directory.Exists(input))
        {
            await BatchConvertAsync(converter, input, output ?? "./converted");
            return 0;
        }

        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"Error: input file or directory '{input}' not found.");
            return 1;
        }

        var outputPath = output ?? Path.Combine(
            Path.GetDirectoryName(input) ?? ".",
            Path.GetFileNameWithoutExtension(input) + "_cx.json");

        await ConvertFileAsync(converter, input, outputPath);
        return 0;
    }

    private static async Task ConvertFileAsync(IGrafanaToCxConverter converter, string inputPath, string outputPath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(inputPath);
            var result = converter.Convert(json);
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(outputPath, result);
            Console.WriteLine($"Converted: {inputPath} -> {outputPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error converting '{inputPath}': {ex.Message}");
        }
    }

    private static async Task BatchConvertAsync(IGrafanaToCxConverter converter, string inputDir, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var files = Directory.GetFiles(inputDir, "*.json", SearchOption.AllDirectories);

        if (files.Length == 0)
        {
            Console.Error.WriteLine($"No JSON files found in '{inputDir}'.");
            return;
        }

        foreach (var file in files)
        {
            var outputFile = Path.Combine(outputDir, Path.GetFileName(file));
            await ConvertFileAsync(converter, file, outputFile);
        }
    }

    // ── Assess ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reports how a set of Grafana dashboards would fare, without uploading anything.
    /// Accepts a directory of dashboard JSON or a backup .zip.
    /// </summary>
    public async Task<int> RunAssessAsync(
        string input, string? output, string? profile, string? region, string? format = null)
    {
        if (!TryParseReportFormat(format, out var reportFormat))
        {
            Console.Error.WriteLine($"Error: unknown format '{format}'. Use 'text' or 'markdown'.");
            return 1;
        }

        var sources = LoadDashboardSources(input);
        if (sources is null)
            return 1;

        if (sources.Count == 0)
        {
            Console.Error.WriteLine($"No dashboard JSON found in '{input}'.");
            return 1;
        }

        // Validation against the live API is a bonus, not a requirement: without it the report
        // still says what conversion loses, it just cannot say what Coralogix would refuse.
        var checker = new CxCliDashboardChecker(
            _loggerFactory.CreateLogger<CxCliDashboardChecker>(),
            Environment.GetEnvironmentVariable("CX_API_KEY") ?? string.Empty,
            region ?? "eu1",
            profile);

        if (!checker.IsInstalled)
            Console.WriteLine("cx CLI not found — assessing conversion only, without API validation.");

        var assessor = new MigrationAssessor(CreateConverter(), checker);
        var assessments = new List<DashboardAssessment>(sources.Count);

        Console.WriteLine($"Assessing {sources.Count} dashboard(s)...");
        foreach (var (name, json) in sources)
            assessments.Add(await assessor.AssessAsync(name, json));

        var report = AssessmentReport.Build(assessments, reportFormat);
        Console.WriteLine();
        Console.WriteLine(report);

        if (!string.IsNullOrWhiteSpace(output))
        {
            await File.WriteAllTextAsync(output, report);
            Console.WriteLine($"Report written to {Path.GetFullPath(output)}");
        }

        // Non-zero when anything would be refused outright, so this can gate a pipeline.
        return assessments.Any(a => a.Verdict is AssessmentVerdict.Rejected or AssessmentVerdict.Failed)
            ? 1
            : 0;
    }

    private static bool TryParseReportFormat(string? format, out AssessmentReportFormat parsed)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            parsed = AssessmentReportFormat.Text;
            return true;
        }

        return Enum.TryParse(format, ignoreCase: true, out parsed);
    }

    private static List<(string Name, string Json)>? LoadDashboardSources(string input)
    {
        var sources = new List<(string, string)>();

        if (Directory.Exists(input))
        {
            foreach (var file in Directory.GetFiles(input, "*.json", SearchOption.AllDirectories).OrderBy(f => f))
                sources.Add((Path.GetRelativePath(input, file), File.ReadAllText(file)));

            return sources;
        }

        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"Error: '{input}' not found.");
            return null;
        }

        if (input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(input);
            foreach (var entry in archive.Entries.OrderBy(e => e.FullName))
            {
                if (!entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;
                // A backup archive carries a _manifest.json describing the backup itself.
                if (Path.GetFileName(entry.FullName).StartsWith('_'))
                    continue;

                using var reader = new StreamReader(entry.Open());
                sources.Add((entry.FullName, reader.ReadToEnd()));
            }

            return sources;
        }

        sources.Add((Path.GetFileName(input), File.ReadAllText(input)));
        return sources;
    }

    // ── Verify ──────────────────────────────────────────────────────────────

    public async Task<int> RunVerifyAsync(string input, string endpoint, string? dashboardId)
    {
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"Error: input file '{input}' not found.");
            return 1;
        }

        var converter = CreateConverter();
        var grafanaJson = await File.ReadAllTextAsync(input);
        var converted = converter.ConvertToJObject(grafanaJson);

        var expectedSections = converted["layout"]?["sections"] as Newtonsoft.Json.Linq.JArray ?? [];
        var expectedWidgets = expectedSections
            .SelectMany(s => s["rows"] as Newtonsoft.Json.Linq.JArray ?? [])
            .SelectMany(r => r["widgets"] as Newtonsoft.Json.Linq.JArray ?? [])
            .ToList();
        var expectedCount = expectedWidgets.Count;

        Console.WriteLine($"Grafana dashboard : {Path.GetFileName(input)}");
        Console.WriteLine($"Expected widgets  : {expectedCount}");
        Console.WriteLine($"Expected sections : {expectedSections.Count}");

        foreach (var section in expectedSections)
        {
            var name = section["options"]?["custom"]?["name"]?.ToString() ?? "(unnamed)";
            var wCount = (section["rows"] as Newtonsoft.Json.Linq.JArray ?? [])
                .SelectMany(r => r["widgets"] as Newtonsoft.Json.Linq.JArray ?? []).Count();
            Console.WriteLine($"  Section \"{name}\": {wCount} widget(s)");
        }

        if (string.IsNullOrEmpty(dashboardId))
        {
            Console.WriteLine();
            Console.WriteLine("No --dashboard-id provided. Skipping CX comparison.");
            return 0;
        }

        var cxApiKey = Environment.GetEnvironmentVariable("CX_API_KEY");
        if (string.IsNullOrEmpty(cxApiKey))
        {
            Console.Error.WriteLine("Error: CX_API_KEY environment variable is not set.");
            return 1;
        }

        var logger = _loggerFactory.CreateLogger<CoralogixDashboardsClient>();
        using var cxClient = new CoralogixDashboardsClient(logger, endpoint, cxApiKey);
        var cxDashboard = await cxClient.GetDashboardByIdAsync(dashboardId);

        if (cxDashboard == null)
        {
            Console.Error.WriteLine($"Failed to fetch dashboard '{dashboardId}' from Coralogix.");
            return 1;
        }

        var cxSections = cxDashboard["layout"]?["sections"] as Newtonsoft.Json.Linq.JArray ?? [];
        var cxWidgets = cxSections
            .SelectMany(s => s["rows"] as Newtonsoft.Json.Linq.JArray ?? [])
            .SelectMany(r => r["widgets"] as Newtonsoft.Json.Linq.JArray ?? [])
            .ToList();
        var actualCount = cxWidgets.Count;

        var grafanaSource = Newtonsoft.Json.Linq.JObject.Parse(grafanaJson);
        var comparison = DashboardComparator.Compare(grafanaSource, cxDashboard, 80.0);

        Console.WriteLine();
        Console.WriteLine($"CX dashboard ID   : {dashboardId}");
        Console.WriteLine($"Actual widgets    : {actualCount}");
        Console.WriteLine($"Comparator coverage: {comparison.Coverage:F1}% (threshold {comparison.CoverageThreshold:F1}%)");

        if (!comparison.Passed)
        {
            Console.WriteLine("[FAIL] Comparator coverage is below threshold.");
            foreach (var failed in comparison.Widgets.Where(w => !w.IsWorking))
            {
                Console.WriteLine($"  - {failed.PanelTitle}: {failed.Notes}");
            }
            return 1;
        }

        if (actualCount == expectedCount)
        {
            Console.WriteLine($"[PASS] All {expectedCount} widget(s) present in Coralogix.");
            return 0;
        }

        Console.WriteLine($"[FAIL] Widget count mismatch: expected {expectedCount}, got {actualCount}.");

        var expectedTitles = expectedWidgets
            .Select(w => w.Value<string>("title") ?? "(untitled)")
            .OrderBy(t => t).ToList();
        var actualTitles = cxWidgets
            .Select(w => w.Value<string>("title") ?? "(untitled)")
            .OrderBy(t => t).ToList();
        var missing = expectedTitles.Except(actualTitles).ToList();
        if (missing.Count > 0)
        {
            Console.WriteLine($"Missing widgets ({missing.Count}):");
            foreach (var t in missing)
                Console.WriteLine($"  - {t}");
        }

        return 1;
    }

    // ── Backup ───────────────────────────────────────────────────────────────

    private const string DefaultBackupFile = "grafana-backup.zip";

    /// <summary>
    /// Downloads Grafana dashboards into a local ZIP and stops there — no conversion,
    /// no Coralogix connection. This is the backup half of <c>migrate</c> on its own.
    /// </summary>
    public async Task<int> RunBackupAsync(string settingsFile, string? output, string? regionOverride, bool interactive)
    {
        MigrationSettings settings;

        if (File.Exists(settingsFile))
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.GetFullPath(settingsFile), optional: false)
                .Build();
            settings = configuration.Get<MigrationSettings>() ?? new MigrationSettings();
        }
        else if (!string.IsNullOrWhiteSpace(regionOverride))
        {
            // --region makes the settings file optional: everything else backup needs has a default.
            Console.WriteLine($"Settings file '{settingsFile}' not found — using defaults with region '{regionOverride}'.");
            settings = new MigrationSettings();
        }
        else
        {
            Console.Error.WriteLine($"Error: settings file '{settingsFile}' not found. Pass --settings <path> or --region <code>.");
            return 1;
        }

        var region = string.IsNullOrWhiteSpace(regionOverride) ? settings.Grafana.Region : regionOverride;

        string grafanaEndpoint;
        try
        {
            grafanaEndpoint = RegionMapper.ResolveGrafana(region);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        var grafanaApiKey = Environment.GetEnvironmentVariable("GRAFANA_API_KEY");
        if (string.IsNullOrWhiteSpace(grafanaApiKey))
            grafanaApiKey = settings.Credentials.GrafanaApiKey;
        if (string.IsNullOrWhiteSpace(grafanaApiKey))
        {
            Console.Error.WriteLine("Error: Grafana API key is required. Set GRAFANA_API_KEY or provide credentials.grafanaApiKey in settings.");
            return 1;
        }

        var backupFile = !string.IsNullOrWhiteSpace(output)
            ? output
            : !string.IsNullOrWhiteSpace(settings.Migration.BackupFile)
                ? settings.Migration.BackupFile
                : DefaultBackupFile;

        using var grafanaClient = new GrafanaClient(
            _loggerFactory.CreateLogger<GrafanaClient>(),
            grafanaEndpoint,
            grafanaApiKey);

        Console.WriteLine($"Fetching folders from {grafanaEndpoint} ...");

        // In interactive mode the on-screen picker is the filter, so ask for every folder.
        var folderFilter = interactive ? Array.Empty<string>() : (IReadOnlyList<string>)settings.Grafana.Folders;

        List<GrafanaFolder> folders;
        try
        {
            folders = await grafanaClient.GetFoldersAsync(folderFilter);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A rejected key can come back as a 2xx non-JSON body, which the client parses eagerly.
            Console.Error.WriteLine($"Error: could not read folders from Grafana — {ex.Message}");
            Console.Error.WriteLine("Check that GRAFANA_API_KEY is valid for this region.");
            return 1;
        }

        if (folders.Count == 0)
        {
            Console.Error.WriteLine("No Grafana folders found (check the API key, region, and grafana.folders filter).");
            return 1;
        }

        if (interactive)
        {
            var selected = MultiSelectWithFallback.SelectRequired(
                "Select folders to back up",
                folders,
                f => f.Title);

            if (selected.Count == 0)
            {
                Console.Error.WriteLine("No folders selected.");
                return 1;
            }

            folders = selected.ToList();
        }

        Console.WriteLine($"Backing up {folders.Count} folder(s) to '{backupFile}' ...");

        var backupService = new GrafanaDashboardBackupService(
            grafanaClient,
            _loggerFactory.CreateLogger<GrafanaDashboardBackupService>());

        GrafanaDashboardBackupResult result;
        try
        {
            result = await backupService.BackupAsync(folders, backupFile);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Error: backup failed — {ex.Message}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("══ Backup Summary ═════════════════════════");
        Console.WriteLine($"  Archive               : {Path.GetFullPath(backupFile)}");
        Console.WriteLine($"  Folders               : {folders.Count}");
        Console.WriteLine($"  Dashboards saved      : {result.SavedDashboards}/{result.TotalDashboards}");

        if (result.FailedFolders.Count > 0)
            Console.WriteLine($"  Folders skipped       : {string.Join(", ", result.FailedFolders)}");
        if (result.FailedDashboards.Count > 0)
            Console.WriteLine($"  Dashboards skipped    : {string.Join(", ", result.FailedDashboards)}");

        if (!result.Success)
        {
            Console.Error.WriteLine("Backup incomplete — see _manifest.json inside the archive.");
            return 1;
        }

        if (result.TotalDashboards == 0)
            Console.WriteLine("  Note                  : selected folders contain no dashboards.");

        return 0;
    }

    // ── Migrate ──────────────────────────────────────────────────────────────

    public async Task<int> RunMigrateAsync(string settingsFile, bool interactive)
    {
        if (interactive)
            return await RunInteractiveConsoleAsync(settingsFile);

        if (!File.Exists(settingsFile))
        {
            Console.Error.WriteLine($"Error: settings file '{settingsFile}' not found.");
            return 1;
        }

        var absoluteSettingsPath = Path.GetFullPath(settingsFile);
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(absoluteSettingsPath, optional: false)
            .Build();
        var settings = configuration.Get<MigrationSettings>() ?? new MigrationSettings();

        var grafanaApiKey = Environment.GetEnvironmentVariable("GRAFANA_API_KEY");
        if (string.IsNullOrWhiteSpace(grafanaApiKey))
            grafanaApiKey = settings.Credentials.GrafanaApiKey;
        if (string.IsNullOrWhiteSpace(grafanaApiKey))
        {
            Console.Error.WriteLine("Error: Grafana API key is required. Set GRAFANA_API_KEY or provide credentials.grafanaApiKey in settings.");
            return 1;
        }

        var cxApiKey = Environment.GetEnvironmentVariable("CX_API_KEY");
        if (string.IsNullOrWhiteSpace(cxApiKey))
            cxApiKey = settings.Credentials.CxApiKey;
        if (string.IsNullOrWhiteSpace(cxApiKey))
        {
            Console.Error.WriteLine("Error: Coralogix API key is required. Set CX_API_KEY or provide credentials.cxApiKey in settings.");
            return 1;
        }

        return await ExecuteMigrationAsync(settingsFile, grafanaApiKey, cxApiKey, promptInteractive: false);
    }

    public async Task<int> ExecuteMigrationAsync(string settingsFile, string grafanaApiKey, string cxApiKey, bool promptInteractive)
    {
        if (!File.Exists(settingsFile))
        {
            Console.Error.WriteLine($"Error: settings file '{settingsFile}' not found.");
            return 1;
        }

        var absoluteSettingsPath = Path.GetFullPath(settingsFile);
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(absoluteSettingsPath, optional: false)
            .Build();

        var settings = configuration.Get<MigrationSettings>() ?? new MigrationSettings();

        string cxEndpoint;
        string grafanaEndpoint;
        try
        {
            cxEndpoint = RegionMapper.Resolve(settings.Coralogix.Region);
            grafanaEndpoint = RegionMapper.ResolveGrafana(settings.Grafana.Region);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        using var grafanaClient = new GrafanaClient(
            _loggerFactory.CreateLogger<GrafanaClient>(),
            grafanaEndpoint,
            grafanaApiKey);

        using var cxFoldersClient = new CoralogixFoldersClient(
            _loggerFactory.CreateLogger<CoralogixFoldersClient>(),
            cxEndpoint,
            cxApiKey);

        if (promptInteractive)
        {
            var result = await RunInteractiveFolderSelectionAsync(grafanaClient, cxFoldersClient, settings);
            if (result is null) return 1;
            settings = result;
        }

        using var cxClient = new CoralogixDashboardsClient(
            _loggerFactory.CreateLogger<CoralogixDashboardsClient>(),
            cxEndpoint,
            cxApiKey);

        CoralogixFoldersClient? structureFoldersClient = null;
        if (settings.Coralogix.MigrateFolderStructure)
            structureFoldersClient = cxFoldersClient;

        var mergeOptions = new MultiLuceneMergeOptions(settings.Migration.MultiLuceneMerge.AllowlistedWidgetTypes);
        var converter = CreateConverter(mergeOptions);
        var validator = new DashboardValidator();

        if (promptInteractive && File.Exists(settings.Migration.CheckpointFile))
        {
            var existingCheckpoint = new CheckpointStore(settings.Migration.CheckpointFile);
            await existingCheckpoint.LoadAsync();
            var completedCount = existingCheckpoint.All.Count(e => e.Status == CheckpointStatus.Completed);
            if (completedCount > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Checkpoint '{settings.Migration.CheckpointFile}' already has {completedCount} completed dashboard(s).");

                if (settings.Coralogix.OverwriteExisting)
                {
                    Console.WriteLine("Overwrite mode is ON — completed dashboards will be re-processed and replaced in Coralogix.");
                }
                else
                {
                    Console.WriteLine("Keeping it means those dashboards will be SKIPPED (not re-migrated).");
                    var resetCheckpoint = Prompt.Confirm("Reset checkpoint and re-migrate all dashboards?", defaultValue: false);
                    if (resetCheckpoint)
                    {
                        File.Delete(settings.Migration.CheckpointFile);
                        Console.WriteLine("Checkpoint reset — all dashboards will be migrated fresh.");
                    }
                    else
                    {
                        Console.WriteLine("Keeping checkpoint — only new or failed dashboards will be migrated.");
                    }
                }
                Console.WriteLine();
            }
        }

        var checkpoint = new CheckpointStore(settings.Migration.CheckpointFile);
        var report = new MigrationReport();

        var backupService = new GrafanaDashboardBackupService(
            grafanaClient,
            _loggerFactory.CreateLogger<GrafanaDashboardBackupService>());

        // Optional pre-upload validation against the live API; a no-op unless `cx` is installed.
        var cxChecker = new CxCliDashboardChecker(
            _loggerFactory.CreateLogger<CxCliDashboardChecker>(),
            cxApiKey,
            settings.Coralogix.Region,
            settings.Migration.CxCliProfile);
        if (cxChecker.IsInstalled)
            Console.WriteLine("cx CLI detected — dashboards will be validated before upload.");

        var orchestrator = new MigrationOrchestrator(
            grafanaClient,
            converter,
            cxClient,
            validator,
            checkpoint,
            report,
            settings,
            _loggerFactory.CreateLogger<MigrationOrchestrator>(),
            structureFoldersClient,
            backupService,
            cxChecker);

        await orchestrator.RunAsync();
        return 0;
    }

    private async Task<MigrationSettings?> RunInteractiveFolderSelectionAsync(
        GrafanaClient grafanaClient,
        CoralogixFoldersClient cxFoldersClient,
        MigrationSettings baseSettings)
    {
        Console.WriteLine("Fetching folders from Grafana...");
        var folders = await grafanaClient.GetFoldersAsync([], CancellationToken.None);

        if (folders.Count == 0)
        {
            Console.Error.WriteLine("No folders found in Grafana.");
            return null;
        }

        var folderChoices = folders.Select(f => f.Title).ToList();
        var selectedFolderNames = Prompt.MultiSelect("Select folders to migrate", folderChoices).ToList();

        if (selectedFolderNames.Count == 0)
        {
            Console.Error.WriteLine("No folders selected.");
            return null;
        }

        Console.WriteLine();
        Console.WriteLine("Fetching Coralogix folders...");
        var cxFolders = await cxFoldersClient.ListFoldersAsync();
        var folderMappings = new Dictionary<string, string?>();

        var strategyChoices = new[] { "Nest all under a parent CX folder (preserves structure)", "Map each Grafana folder individually" };
        var strategy = Prompt.Select("Folder nesting strategy", strategyChoices);

        if (strategy == strategyChoices[0])
        {
            var rootFolders = cxFolders.Where(f => f.ParentId is null).ToList();
            var parentChoices = new[] { "+ Create new folder" }
                .Concat(rootFolders.Select(f => f.Name))
                .ToList();
            var parentChoice = Prompt.Select("Select or create parent CX folder", parentChoices);

            string parentFolderName;
            string parentFolderId;

            if (parentChoice == "+ Create new folder")
            {
                var defaultName = Prompt.Input<string>("New parent folder name", defaultValue: "Grafana Migration");
                Console.Write($"  Creating parent folder '{defaultName}'... ");
                var newParentId = await cxFoldersClient.GetOrCreateFolderAsync(defaultName);
                if (newParentId is null)
                {
                    Console.Error.WriteLine($"Failed to create parent folder '{defaultName}'.");
                    return null;
                }
                Console.WriteLine($"OK (id: {newParentId})");
                parentFolderName = defaultName;
                parentFolderId = newParentId;
                cxFolders = await cxFoldersClient.ListFoldersAsync();
            }
            else
            {
                var chosen = rootFolders.First(f => f.Name == parentChoice);
                parentFolderName = chosen.Name;
                parentFolderId = chosen.Id;
                Console.WriteLine($"  → Using existing folder '{parentFolderName}'");
            }

            Console.WriteLine();
            Console.WriteLine($"Creating sub-folders under '{parentFolderName}'...");
            foreach (var grafanaFolderName in selectedFolderNames)
            {
                Console.Write($"  '{grafanaFolderName}'... ");
                var subFolderId = await cxFoldersClient.GetOrCreateFolderAsync(grafanaFolderName, parentFolderId);
                if (subFolderId is null)
                {
                    Console.Error.WriteLine($"Failed to create sub-folder '{grafanaFolderName}'.");
                    return null;
                }
                Console.WriteLine($"OK (id: {subFolderId})");
                folderMappings[grafanaFolderName] = subFolderId;
            }
            cxFolders = await cxFoldersClient.ListFoldersAsync();
        }
        else
        {
            foreach (var grafanaFolderName in selectedFolderNames)
            {
                var mapChoices = new[] { "+ Create new folder" }
                    .Concat(cxFolders.Select(f => f.Name))
                    .Concat(["(none — no folder)"])
                    .ToList();
                var mapChoice = Prompt.Select($"Map '{grafanaFolderName}' → Coralogix folder", mapChoices);

                string? cxFolderId;
                if (mapChoice == "(none — no folder)")
                {
                    cxFolderId = null;
                }
                else if (mapChoice == "+ Create new folder")
                {
                    var newName = Prompt.Input<string>("New folder name", defaultValue: grafanaFolderName);
                    Console.Write($"  Creating folder '{newName}'... ");
                    cxFolderId = await cxFoldersClient.GetOrCreateFolderAsync(newName);
                    if (cxFolderId is null)
                    {
                        Console.Error.WriteLine($"Failed to create folder '{newName}'.");
                        return null;
                    }
                    Console.WriteLine($"OK (id: {cxFolderId})");
                    cxFolders = await cxFoldersClient.ListFoldersAsync();
                }
                else
                {
                    cxFolderId = cxFolders.First(f => f.Name == mapChoice).Id;
                }
                folderMappings[grafanaFolderName] = cxFolderId;
            }
        }

        Console.WriteLine();
        Console.WriteLine("Migration plan:");
        foreach (var (grafanaName, cxId) in folderMappings)
        {
            var cxLabel = cxId is null
                ? "(no folder)"
                : cxFolders.FirstOrDefault(f => f.Id == cxId)?.Name ?? cxId;
            Console.WriteLine($"  Grafana '{grafanaName}'  →  CX '{cxLabel}'");
        }

        var overwriteExisting = Prompt.Confirm("Overwrite dashboards that already exist in Coralogix?", defaultValue: false);
        if (overwriteExisting)
            Console.WriteLine("  → Existing dashboards will be replaced.");
        else
            Console.WriteLine("  → Existing dashboards will be skipped.");

        var proceed = Prompt.Confirm("Proceed with migration?", defaultValue: true);
        if (!proceed)
        {
            Console.WriteLine("Aborted.");
            return null;
        }

        return new MigrationSettings
        {
            Grafana = new GrafanaSettings
            {
                Region = baseSettings.Grafana.Region,
                Folders = selectedFolderNames
            },
            Coralogix = new CoralogixSettings
            {
                Region = baseSettings.Coralogix.Region,
                FolderId = baseSettings.Coralogix.FolderId,
                IsLocked = baseSettings.Coralogix.IsLocked,
                MigrateFolderStructure = false,
                FolderMappings = folderMappings,
                OverwriteExisting = overwriteExisting
            },
            Migration = baseSettings.Migration
        };
    }

    // ── Interactive console ───────────────────────────────────────────────────

    public async Task<int> RunInteractiveConsoleAsync(string settingsFile)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("╔══════════════════════════════════════════╗");
        Console.WriteLine("║  Grafana → Coralogix Dashboard Converter ║");
        Console.WriteLine("╚══════════════════════════════════════════╝");
        Console.WriteLine();

        var config = PromptInput.PromptSessionConfig();
        if (config is null) return 1;

        while (true)
        {
            var selected = PromptMenus.ShowMainMenu(config.CxEndpoint);
            if (selected is null) continue;

            Console.WriteLine();

            switch (selected.Key)
            {
                case "1":
                    await RunConvertMenuAsync();
                    break;
                case "2":
                    await RunPushMenuAsync(config);
                    break;
                case "3":
                    await RunImportMenuAsync(config);
                    break;
                case "4":
                    await RunMigrateMenuAsync(config, settingsFile);
                    break;
                case "5":
                    config = PromptMenus.RunSettingsMenu(config);
                    break;
                case "6":
                    await RunCleanupFoldersMenuAsync(config);
                    break;
                case "7":
                    await RunBackupMenuAsync(settingsFile);
                    break;
                case "0":
                    Console.WriteLine("Goodbye.");
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown option '{selected.Key}'. Enter 0–7.");
                    break;
            }
        }
    }

    private async Task RunConvertMenuAsync()
    {
        var input = Prompt.Input<string>("Input file or directory", validators: [Validators.Required()]);
        var output = Prompt.Input<string>("Output path (Enter = default)", defaultValue: string.Empty);
        await RunConvertAsync(input, string.IsNullOrEmpty(output) ? null : output);
    }

    private async Task RunPushMenuAsync(SessionConfig config)
    {
        var input = Prompt.Input<string>("Input Grafana JSON file", validators: [Validators.Required()]);
        await RunPushAsync(input, config.CxEndpoint, config.CxApiKey,
            folderId: null, folderName: null, nameOverride: null, interactive: true);
    }

    private async Task RunImportMenuAsync(SessionConfig config)
    {
        var dir = Prompt.Input<string>("Root directory", defaultValue: ".");
        await RunImportAsync(dir, config.CxEndpoint, config.CxApiKey, interactive: true);
    }

    private async Task RunMigrateMenuAsync(SessionConfig config, string settingsFile)
    {
        var grafanaKey = Environment.GetEnvironmentVariable("GRAFANA_API_KEY");
        if (string.IsNullOrEmpty(grafanaKey))
        {
            grafanaKey = Prompt.Password("Grafana API key", validators: [Validators.Required()]);
            if (string.IsNullOrEmpty(grafanaKey))
            {
                Console.Error.WriteLine("Grafana API key is required.");
                return;
            }
        }
        else
        {
            Console.WriteLine("Using GRAFANA_API_KEY from environment.");
        }

        settingsFile = Prompt.Input<string>("Settings file", defaultValue: settingsFile);
        await ExecuteMigrationAsync(settingsFile, grafanaKey, config.CxApiKey, promptInteractive: true);
    }

    private async Task RunBackupMenuAsync(string settingsFile)
    {
        var grafanaKey = Environment.GetEnvironmentVariable("GRAFANA_API_KEY");
        if (string.IsNullOrEmpty(grafanaKey))
        {
            grafanaKey = Prompt.Password("Grafana API key", validators: [Validators.Required()]);
            if (string.IsNullOrEmpty(grafanaKey))
            {
                Console.Error.WriteLine("Grafana API key is required.");
                return;
            }

            // RunBackupAsync reads the key from the environment or the settings file, so hand
            // the prompted value over the same way the rest of the session does.
            Environment.SetEnvironmentVariable("GRAFANA_API_KEY", grafanaKey);
        }
        else
        {
            Console.WriteLine("Using GRAFANA_API_KEY from environment.");
        }

        settingsFile = Prompt.Input<string>("Settings file", defaultValue: settingsFile);
        var output = Prompt.Input<string>("Output ZIP (Enter = use settings)", defaultValue: string.Empty);

        await RunBackupAsync(
            settingsFile,
            string.IsNullOrWhiteSpace(output) ? null : output,
            regionOverride: null,
            interactive: true);
    }

    private async Task RunCleanupFoldersMenuAsync(SessionConfig config)
    {
        using var foldersClient = new CoralogixFoldersClient(
            _loggerFactory.CreateLogger<CoralogixFoldersClient>(), config.CxEndpoint, config.CxApiKey);
        using var dashboardsClient = new CoralogixDashboardsClient(
            _loggerFactory.CreateLogger<CoralogixDashboardsClient>(), config.CxEndpoint, config.CxApiKey);

        var backupService = new CoralogixDashboardBackupService(
            dashboardsClient,
            _loggerFactory.CreateLogger<CoralogixDashboardBackupService>());
        var cleanupService = new FolderCleanupService(
            dashboardsClient,
            foldersClient,
            backupService,
            _loggerFactory.CreateLogger<FolderCleanupService>());

        Console.WriteLine("Fetching Coralogix folders...");
        var folders = await foldersClient.ListFoldersAsync();
        if (folders.Count == 0)
        {
            Console.WriteLine("No Coralogix folders found.");
            return;
        }

        var flatFolders = BuildFlatFolderList(folders);
        var selectedFolders = MultiSelectWithFallback.SelectRequired(
            "Select folders for cleanup",
            flatFolders,
            x => x.Display);
        if (selectedFolders.Count == 0)
        {
            Console.WriteLine("No folders selected.");
            return;
        }

        var canonicalSelectedRoots = FolderSelectionNormalizer.NormalizeSelectedRoots(
            selectedFolders.Select(x => x.Folder).ToList(),
            folders);
        if (canonicalSelectedRoots.Count == 0)
        {
            Console.WriteLine("No folders selected.");
            return;
        }

        Console.Clear();
        Console.WriteLine("Selected root folder(s) for cleanup:");
        foreach (var selectedRoot in canonicalSelectedRoots)
            Console.WriteLine($"  - {selectedRoot.Name} ({selectedRoot.Id})");
        Console.WriteLine();
        Console.WriteLine("Loading nested folders and dashboards...");

        // Build folder tree to find all descendants
        var foldersById = folders.ToDictionary(f => f.Id, f => f);
        var childrenByParent = folders
            .Where(f => f.ParentId is not null)
            .GroupBy(f => f.ParentId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Collect all folders to process: selected + all descendants recursively
        var foldersToProcess = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var foldersToProcessList = new List<CxFolderItem>();

        void AddFolderAndDescendants(CxFolderItem folder)
        {
            if (!foldersToProcess.Add(folder.Id))
                return; // Already added

            foldersToProcessList.Add(folder);

            if (childrenByParent.TryGetValue(folder.Id, out var children))
            {
                foreach (var child in children)
                {
                    AddFolderAndDescendants(child);
                }
            }
        }

        foreach (var selectedRoot in canonicalSelectedRoots)
            AddFolderAndDescendants(selectedRoot);

        // Collect all dashboards from all folders
        var allDashboards = new List<(CxFolderItem Folder, DashboardCatalogItem Dashboard)>();
        foreach (var folder in foldersToProcessList)
        {
            var dashboards = await dashboardsClient.GetCatalogItemsByFolderAsync(folder.Id);
            foreach (var dashboard in dashboards)
            {
                allDashboards.Add((folder, dashboard));
            }
        }

        // Calculate depth for display
        var folderDepthMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int GetDepth(CxFolderItem folder)
        {
            if (folderDepthMap.TryGetValue(folder.Id, out var depth))
                return depth;

            if (folder.ParentId is null || !foldersById.TryGetValue(folder.ParentId, out var parent))
            {
                depth = 0;
            }
            else
            {
                depth = GetDepth(parent) + 1;
            }

            folderDepthMap[folder.Id] = depth;
            return depth;
        }

        foreach (var folder in foldersToProcessList)
        {
            GetDepth(folder);
        }

        var foldersSortedByDepth = foldersToProcessList
            .OrderBy(f => folderDepthMap[f.Id])
            .ThenBy(f => f.Name)
            .ToList();

        Console.Clear();
        Console.WriteLine("Cleanup plan:");
        Console.WriteLine($"  Selected root folders: {canonicalSelectedRoots.Count}");
        foreach (var selectedRoot in canonicalSelectedRoots)
            Console.WriteLine($"    - {selectedRoot.Name} ({selectedRoot.Id})");
        Console.WriteLine($"  Total folders to delete: {foldersToProcessList.Count} (including {foldersToProcessList.Count - 1} nested folder(s))");
        Console.WriteLine($"  Total dashboards to backup/delete: {allDashboards.Count}");
        Console.WriteLine();

        if (foldersToProcessList.Count > 1)
        {
            Console.WriteLine("  Folders to delete:");
            foreach (var folder in foldersSortedByDepth)
            {
                var depth = folderDepthMap[folder.Id];
                var indent = new string(' ', depth * 2);
                var shortId = folder.Id.Length > 8 ? folder.Id[..8] : folder.Id;
                var isSelected = canonicalSelectedRoots.Any(root =>
                    string.Equals(root.Id, folder.Id, StringComparison.OrdinalIgnoreCase));
                var marker = isSelected ? "*" : " ";
                Console.WriteLine($"    {marker} {indent}{folder.Name} [{shortId}]");
            }
            Console.WriteLine();
        }

        if (allDashboards.Count > 0)
        {
            Console.WriteLine("  Dashboards:");
            var dashboardIndex = 1;
            foreach (var folder in foldersSortedByDepth)
            {
                var folderDashboards = allDashboards.Where(d => d.Folder.Id == folder.Id).ToList();
                if (folderDashboards.Count == 0)
                    continue;

                var depth = folderDepthMap[folder.Id];
                var indent = new string(' ', depth * 2);
                if (foldersToProcessList.Count > 1)
                {
                    Console.WriteLine($"    {indent}In '{folder.Name}':");
                }

                foreach (var (_, dashboard) in folderDashboards)
                {
                    var shortId = dashboard.Id.Length > 8 ? dashboard.Id[..8] : dashboard.Id;
                    Console.WriteLine($"    {indent}  {dashboardIndex,3}. {dashboard.Name} [{shortId}]");
                    dashboardIndex++;
                }
            }
        }
        else
        {
            Console.WriteLine("  (No dashboards in selected folder or nested folders)");
        }

        var defaultBackupPath = $"cx-folder-delete-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip";
        var backupPath = Prompt.Input<string>("Backup ZIP path", defaultValue: defaultBackupPath);
        backupPath = Path.GetFullPath(backupPath);

        if (File.Exists(backupPath))
        {
            var overwrite = Prompt.Confirm("Backup file already exists. Overwrite?", defaultValue: false);
            if (!overwrite)
            {
                Console.WriteLine("Aborted.");
                return;
            }
        }

        var proceed = Prompt.Confirm("Proceed with backup and deletion? (Backup is mandatory)", defaultValue: false);
        if (!proceed)
        {
            Console.WriteLine("Aborted.");
            return;
        }

        var result = await cleanupService.CleanupAsync(canonicalSelectedRoots, backupPath);

        Console.WriteLine();
        Console.WriteLine("Cleanup result:");
        Console.WriteLine($"  Backup file           : {result.BackupFilePath}");
        Console.WriteLine($"  Backup succeeded      : {result.BackupSucceeded}");
        Console.WriteLine($"  Selected folders      : {result.SelectedFolders}");
        Console.WriteLine($"  Backed up dashboards  : {result.BackedUpDashboards}");
        Console.WriteLine($"  Deleted dashboards    : {result.DeletedDashboards}");
        Console.WriteLine($"  Failed dashboard dels : {result.FailedDashboardDeletions}");
        Console.WriteLine($"  Deleted folders       : {result.DeletedFolders}");
        Console.WriteLine($"  Failed folder dels    : {result.FailedFolderDeletions}");

        if (!result.BackupSucceeded && result.FailedBackupDashboardIds.Count > 0)
        {
            Console.WriteLine("  Backup failures:");
            foreach (var failedId in result.FailedBackupDashboardIds)
                Console.WriteLine($"    - {failedId}");
        }
    }

    private sealed record FolderSelectItem(CxFolderItem Folder, string Display);

    private sealed record ImportFolderOption(string Dir, int Count, string Display);

    private static List<FolderSelectItem> BuildFlatFolderList(List<CxFolderItem> folders)
    {
        var treeData = BuildFolderTreeData(folders);
        var expanded = new HashSet<string>(treeData.AllById.Keys, StringComparer.OrdinalIgnoreCase);
        var rows = BuildVisibleFolderRows(treeData, expanded);
        return rows.Select(r =>
        {
            var shortId = r.Folder.Id.Length > 8 ? r.Folder.Id[..8] : r.Folder.Id;
            var indent = new string(' ', r.Depth * 2);
            var marker = r.HasChildren ? "[-]" : "   ";
            return new FolderSelectItem(r.Folder, $"{indent}{marker} {r.Folder.Name} [{shortId}]");
        }).ToList();
    }

    private static (Dictionary<string, CxFolderItem> AllById, Dictionary<string, List<CxFolderItem>> ChildrenByParent, List<CxFolderItem> Roots) BuildFolderTreeData(List<CxFolderItem> folders)
    {
        if (folders.Count == 0)
            return ([], [], []);

        var byId = folders.ToDictionary(f => f.Id, f => f);
        var childrenByParent = folders
            .GroupBy(f => f.ParentId ?? "__ROOT__")
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Name).ThenBy(x => x.Id).ToList());

        var roots = folders
            .Where(f => f.ParentId is null || !byId.ContainsKey(f.ParentId))
            .OrderBy(f => f.Name)
            .ThenBy(f => f.Id)
            .ToList();

        return (byId, childrenByParent, roots);
    }

    private static List<(CxFolderItem Folder, int Depth, bool HasChildren, bool IsExpanded, string? ParentId)> BuildVisibleFolderRows(
        (Dictionary<string, CxFolderItem> AllById, Dictionary<string, List<CxFolderItem>> ChildrenByParent, List<CxFolderItem> Roots) treeData,
        HashSet<string> expanded)
    {
        var rows = new List<(CxFolderItem Folder, int Depth, bool HasChildren, bool IsExpanded, string? ParentId)>(treeData.AllById.Count);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddNode(CxFolderItem folder, int depth)
        {
            if (!visited.Add(folder.Id))
                return;

            var hasChildren = treeData.ChildrenByParent.TryGetValue(folder.Id, out var children) && children.Count > 0;
            var isExpanded = hasChildren && expanded.Contains(folder.Id);
            rows.Add((folder, depth, hasChildren, isExpanded, folder.ParentId));

            if (!isExpanded || children is null)
                return;

            foreach (var child in children)
                AddNode(child, depth + 1);
        }

        foreach (var root in treeData.Roots)
            AddNode(root, 0);

        foreach (var orphan in treeData.AllById.Values.OrderBy(f => f.Name).ThenBy(f => f.Id))
        {
            if (!visited.Contains(orphan.Id))
                AddNode(orphan, 0);
        }

        return rows;
    }

    // ── Push ──────────────────────────────────────────────────────────────────

    public async Task<int> RunPushAsync(string input, string endpoint, string apiKey, string? folderId, string? folderName, string? nameOverride, bool interactive = false)
    {
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"Error: input file '{input}' not found.");
            return 1;
        }

        if (folderId != null && folderName != null)
        {
            Console.Error.WriteLine("Error: folder ID and folder name cannot both be specified.");
            return 1;
        }

        var converter = CreateConverter();
        using var client = new CoralogixDashboardsClient(
            _loggerFactory.CreateLogger<CoralogixDashboardsClient>(), endpoint, apiKey);
        using var foldersClient = new CoralogixFoldersClient(
            _loggerFactory.CreateLogger<CoralogixFoldersClient>(), endpoint, apiKey);

        if (interactive)
        {
            var defaultName = nameOverride ?? Path.GetFileNameWithoutExtension(input);
            var selection = await RunInteractivePushSelectionAsync(foldersClient, defaultName);
            if (selection is null) return 1;
            (folderId, nameOverride) = selection.Value;
        }
        else if (folderName != null)
        {
            folderId = await foldersClient.GetOrCreateFolderAsync(folderName);
            if (folderId == null)
            {
                Console.Error.WriteLine($"Error: could not resolve or create folder '{folderName}'.");
                return 1;
            }
            Console.WriteLine($"Resolved folder '{folderName}' -> {folderId}");
        }

        try
        {
            var json = await File.ReadAllTextAsync(input);
            var options = new ConversionOptions { FolderId = folderId };
            var dashboard = converter.ConvertToJObject(json, options);

            if (!string.IsNullOrWhiteSpace(nameOverride))
                dashboard["name"] = nameOverride;

            var dashboardName = dashboard.Value<string>("name") ?? string.Empty;

            Console.WriteLine($"Fetching existing dashboards from {endpoint}...");
            var catalog = await client.GetCatalogItemsAsync();

            var conflict = catalog.FirstOrDefault(item =>
                string.Equals(item.Name, dashboardName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.FolderId, folderId, StringComparison.OrdinalIgnoreCase));

            string? dashboardId;

            if (conflict != null)
            {
                var nextVersion = ComputeNextVersion(catalog, dashboardName, folderId);
                var copyName = $"v_{nextVersion}_{dashboardName}";

                Console.WriteLine();
                Console.WriteLine($"Dashboard '{dashboardName}' already exists in this folder.");
                var choices = new[] { "Overwrite existing dashboard", $"Create copy as '{copyName}'", "Quit" };
                var choice = Prompt.Select("Choice", choices);

                if (choice == "Quit")
                {
                    Console.WriteLine("Aborted.");
                    return 0;
                }

                if (choice == "Overwrite existing dashboard")
                {
                    dashboard["id"] = conflict.Id;
                    Console.WriteLine($"Overwriting dashboard '{dashboardName}'...");
                    var replaced = await client.ReplaceDashboardAsync(dashboard, folderId: folderId);
                    if (!replaced)
                    {
                        Console.Error.WriteLine("Failed to overwrite dashboard. Check logs for details.");
                        return 1;
                    }
                    dashboardId = conflict.Id;
                    Console.WriteLine($"Success! Dashboard overwritten. ID: {dashboardId}");
                }
                else
                {
                    dashboard["name"] = copyName;
                    Console.WriteLine($"Creating copy as '{copyName}'...");
                    dashboardId = await client.CreateDashboardAsync(dashboard, folderId: folderId);
                    if (dashboardId == null)
                    {
                        Console.Error.WriteLine("Failed to create dashboard copy. Check logs for details.");
                        return 1;
                    }
                    Console.WriteLine($"Success! Dashboard copy created. ID: {dashboardId}");
                }
            }
            else
            {
                Console.WriteLine($"Pushing dashboard '{dashboardName}' to {endpoint}...");
                dashboardId = await client.CreateDashboardAsync(dashboard, folderId: folderId);
                if (dashboardId == null)
                {
                    Console.Error.WriteLine("Failed to push dashboard. Check logs for details.");
                    return 1;
                }
                Console.WriteLine($"Success! Dashboard ID: {dashboardId}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private async Task<(string? FolderId, string? NameOverride)?> RunInteractivePushSelectionAsync(
        CoralogixFoldersClient foldersClient,
        string defaultName)
    {
        Console.WriteLine("Fetching folders from Coralogix...");
        var folders = await foldersClient.ListFoldersAsync();

        string? selectedFolderId = null;
        if (folders.Count > 0)
        {
            var choices = folders.Select(f => f.Name).Prepend("(none — no folder)").ToList();
            var selected = Prompt.Select("Select folder", choices);
            if (selected != "(none — no folder)")
            {
                selectedFolderId = folders.First(f => f.Name == selected).Id;
            }
        }
        else
        {
            Console.WriteLine("No folders found. The dashboard will be placed outside any folder.");
        }

        var finalName = Prompt.Input<string>("Dashboard name", defaultValue: defaultName);
        return (selectedFolderId, finalName);
    }

    private static int ComputeNextVersion(List<DashboardCatalogItem> catalog, string baseName, string? folderId)
    {
        var versionPattern = new Regex(
            @"^v_(\d+)_" + Regex.Escape(baseName) + "$",
            RegexOptions.IgnoreCase);

        var maxVersion = catalog
            .Where(item => string.Equals(item.FolderId, folderId, StringComparison.OrdinalIgnoreCase))
            .Select(item => versionPattern.Match(item.Name))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(1)
            .Max();

        return maxVersion + 1;
    }

    // ── Import ────────────────────────────────────────────────────────────────

    public async Task<int> RunImportAsync(string? input, string endpoint, string apiKey, bool interactive)
    {
        string rootDir;

        if (string.IsNullOrEmpty(input))
        {
            if (!interactive)
            {
                Console.Error.WriteLine("Error: input directory is required when not using interactive mode.");
                return 1;
            }
            rootDir = Prompt.Input<string>("Root directory containing Grafana dashboards", defaultValue: ".");
        }
        else
        {
            rootDir = input;
        }

        rootDir = Path.GetFullPath(rootDir);

        if (!Directory.Exists(rootDir))
        {
            Console.Error.WriteLine($"Error: directory '{rootDir}' not found.");
            return 1;
        }

        var converter = CreateConverter();

        using var foldersClient = new CoralogixFoldersClient(
            _loggerFactory.CreateLogger<CoralogixFoldersClient>(), endpoint, apiKey);
        using var dashboardsClient = new CoralogixDashboardsClient(
            _loggerFactory.CreateLogger<CoralogixDashboardsClient>(), endpoint, apiKey);

        List<(string LocalDir, string? CxFolderId)> importPlan;

        if (interactive)
        {
            var plan = await RunInteractiveImportSelectionAsync(rootDir, foldersClient);
            if (plan is null) return 1;
            importPlan = plan;
        }
        else
        {
            importPlan = [(rootDir, null)];
        }

        var totalPushed = 0;
        var totalFailed = 0;

        foreach (var (localDir, cxFolderId) in importPlan)
        {
            var jsonFiles = Directory.GetFiles(localDir, "*.json", SearchOption.TopDirectoryOnly);
            if (jsonFiles.Length == 0) continue;

            Console.WriteLine();
            Console.WriteLine($"Importing from '{Path.GetRelativePath(rootDir, localDir)}' ({jsonFiles.Length} file(s))...");

            foreach (var file in jsonFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var options = new ConversionOptions { FolderId = cxFolderId };
                    var dashboard = converter.ConvertToJObject(json, options);
                    var dashboardName = dashboard.Value<string>("name") ?? Path.GetFileNameWithoutExtension(file);

                    var catalog = await dashboardsClient.GetCatalogItemsAsync();
                    var conflict = catalog.FirstOrDefault(item =>
                        string.Equals(item.Name, dashboardName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.FolderId, cxFolderId, StringComparison.OrdinalIgnoreCase));

                    string? dashboardId;
                    if (conflict != null)
                    {
                        dashboard["id"] = conflict.Id;
                        Console.Write($"  [overwrite] {dashboardName} ... ");
                        var replaced = await dashboardsClient.ReplaceDashboardAsync(dashboard, folderId: cxFolderId);
                        dashboardId = replaced ? conflict.Id : null;
                    }
                    else
                    {
                        Console.Write($"  [create]    {dashboardName} ... ");
                        dashboardId = await dashboardsClient.CreateDashboardAsync(dashboard, folderId: cxFolderId);
                    }

                    if (dashboardId == null)
                    {
                        Console.WriteLine("FAILED");
                        totalFailed++;
                        continue;
                    }

                    if (cxFolderId != null)
                        await dashboardsClient.AssignDashboardToFolderAsync(dashboardId, cxFolderId);

                    Console.WriteLine($"OK (id: {dashboardId})");
                    totalPushed++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR: {ex.Message}");
                    totalFailed++;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Import complete. Pushed: {totalPushed}, Failed: {totalFailed}");
        return totalFailed > 0 ? 1 : 0;
    }

    private async Task<List<(string LocalDir, string? CxFolderId)>?> RunInteractiveImportSelectionAsync(
        string rootDir,
        CoralogixFoldersClient foldersClient)
    {
        var dashboardFolders = DiscoverDashboardFolders(rootDir);

        if (dashboardFolders.Count == 0)
        {
            Console.Error.WriteLine($"No JSON files found in '{rootDir}' or its immediate subdirectories.");
            return null;
        }

        var folderOptions = dashboardFolders
            .Select(f => new ImportFolderOption(f.Dir, f.Count, f.Dir == rootDir ? $"(root) [{f.Count} dashboard(s)]" : $"{Path.GetRelativePath(rootDir, f.Dir)} [{f.Count} dashboard(s)]"))
            .ToList();
        var selected = Prompt.MultiSelect("Select folders to import", folderOptions, textSelector: x => x.Display);

        var selectedFolders = selected.Select(x => (x.Dir, x.Count)).ToList();

        if (selectedFolders.Count == 0)
        {
            Console.Error.WriteLine("No folders selected.");
            return null;
        }

        Console.WriteLine();
        Console.WriteLine("Fetching Coralogix folders...");
        var cxFolders = await foldersClient.ListFoldersAsync();

        var plan = new List<(string LocalDir, string? CxFolderId)>();

        foreach (var (dir, count) in selectedFolders)
        {
            var label = dir == rootDir ? "(root)" : Path.GetRelativePath(rootDir, dir);
            var mapChoices = new[] { "+ Create new folder" }
                .Concat(cxFolders.Select(f => f.Name))
                .Concat(["(none — no folder)"])
                .ToList();
            var mapChoice = Prompt.Select($"Map '{label}' ({count} dashboard(s)) → Coralogix folder", mapChoices);

            string? cxFolderId = null;

            if (mapChoice != "(none — no folder)" && mapChoice != "+ Create new folder")
            {
                cxFolderId = cxFolders.First(f => f.Name == mapChoice).Id;
            }
            else if (mapChoice == "+ Create new folder")
            {
                var defaultFolderName = dir == rootDir ? "Imported Dashboards" : Path.GetFileName(dir);
                var folderName = Prompt.Input<string>("New folder name", defaultValue: defaultFolderName);

                Console.Write($"  Creating folder '{folderName}'... ");
                cxFolderId = await foldersClient.GetOrCreateFolderAsync(folderName);
                if (cxFolderId == null)
                {
                    Console.Error.WriteLine($"Failed to create folder '{folderName}'.");
                    return null;
                }
                Console.WriteLine($"OK (id: {cxFolderId})");
                cxFolders = await foldersClient.ListFoldersAsync();
            }

            plan.Add((dir, cxFolderId));
        }

        Console.WriteLine();
        Console.WriteLine("Import plan:");
        foreach (var (localDir, cxFolderId) in plan)
        {
            var localLabel = localDir == rootDir ? "(root)" : Path.GetRelativePath(rootDir, localDir);
            var cxLabel = cxFolderId == null
                ? "(no folder)"
                : (await foldersClient.ListFoldersAsync()).FirstOrDefault(f => f.Id == cxFolderId)?.Name ?? cxFolderId;
            Console.WriteLine($"  {localLabel}  →  {cxLabel}");
        }

        var proceed = Prompt.Confirm("Proceed with import?", defaultValue: true);
        if (!proceed)
        {
            Console.WriteLine("Aborted.");
            return null;
        }

        return plan;
    }

    private static List<(string Dir, int Count)> DiscoverDashboardFolders(string rootDir)
    {
        var result = new List<(string Dir, int Count)>();

        var rootJsonCount = Directory.GetFiles(rootDir, "*.json", SearchOption.TopDirectoryOnly).Length;
        if (rootJsonCount > 0)
            result.Add((rootDir, rootJsonCount));

        foreach (var subDir in Directory.GetDirectories(rootDir).OrderBy(d => d))
        {
            var count = Directory.GetFiles(subDir, "*.json", SearchOption.TopDirectoryOnly).Length;
            if (count > 0)
                result.Add((subDir, count));
        }

        return result;
    }
}
