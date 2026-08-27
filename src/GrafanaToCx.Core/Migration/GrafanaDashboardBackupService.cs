using System.IO.Compression;
using System.Text;
using GrafanaToCx.Core.ApiClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Migration;

public sealed class GrafanaDashboardBackupService
{
    private const string ManifestEntryName = "_manifest.json";
    private static readonly char[] InvalidPathChars = Path.GetInvalidFileNameChars();

    private readonly IGrafanaClient _grafanaClient;
    private readonly ILogger<GrafanaDashboardBackupService> _logger;

    public GrafanaDashboardBackupService(
        IGrafanaClient grafanaClient,
        ILogger<GrafanaDashboardBackupService> logger)
    {
        _grafanaClient = grafanaClient;
        _logger = logger;
    }

    public async Task<GrafanaDashboardBackupResult> BackupAsync(
        IReadOnlyList<GrafanaFolder> folders,
        string backupFilePath,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(backupFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _logger.LogInformation("Starting Grafana dashboard backup to '{BackupFile}'.", backupFilePath);

        var discovered = 0;
        var written = 0;
        var failedDashboards = new List<string>();
        var failedFolders = new List<string>();

        using var stream = new FileStream(backupFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();

            List<GrafanaDashboardRef> dashboards;
            try
            {
                dashboards = await _grafanaClient.GetDashboardsInFolderAsync(folder.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Backup: failed to list dashboards in folder '{Folder}'. Skipping folder.",
                    folder.Title);
                failedFolders.Add(folder.Title);
                continue;
            }

            discovered += dashboards.Count;
            var safeFolder = Sanitize(folder.Title);

            foreach (var dash in dashboards)
            {
                ct.ThrowIfCancellationRequested();

                JObject? json;
                try
                {
                    json = await _grafanaClient.GetDashboardByUidAsync(dash.Uid, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Backup: failed to fetch dashboard '{Title}' ({Uid}). Skipping.",
                        dash.Title, dash.Uid);
                    failedDashboards.Add(dash.Uid);
                    continue;
                }

                if (json is null)
                {
                    _logger.LogWarning(
                        "Backup: dashboard '{Title}' ({Uid}) returned empty response. Skipping.",
                        dash.Title, dash.Uid);
                    failedDashboards.Add(dash.Uid);
                    continue;
                }

                var entryName = $"{safeFolder}/{Sanitize(dash.Title)}_{dash.Uid}.json";
                var entry = zip.CreateEntry(entryName, CompressionLevel.SmallestSize);

                await using var entryStream = entry.Open();
                await using var writer = new StreamWriter(entryStream);
                await writer.WriteAsync(json.ToString(Formatting.Indented));

                written++;
                _logger.LogDebug("Backup: saved '{Entry}'.", entryName);
            }
        }

        // A zero-entry ZipArchive is a 22-byte stub some tools refuse to open. Drop a manifest in
        // so an empty or partial backup is still readable and explains itself.
        if (written == 0 || failedDashboards.Count > 0 || failedFolders.Count > 0)
        {
            var manifest = new BackupManifest
            {
                Expected = discovered,
                Written = written,
                FailedIds = [..failedDashboards],
                FailedFolders = [..failedFolders],
                Note = BuildNote(discovered, failedDashboards.Count, failedFolders.Count)
            };

            var manifestEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.SmallestSize);
            await using (var manifestStream = manifestEntry.Open())
            await using (var manifestWriter = new StreamWriter(manifestStream, Encoding.UTF8))
            {
                await manifestWriter.WriteAsync(JsonConvert.SerializeObject(manifest, Formatting.Indented));
            }
        }

        var result = new GrafanaDashboardBackupResult(discovered, written, failedDashboards, failedFolders);

        if (result.Success)
        {
            _logger.LogInformation(
                "Backup complete: {Written}/{Expected} dashboard(s) saved. Archive: '{BackupFile}'.",
                written, discovered, backupFilePath);
        }
        else
        {
            _logger.LogError(
                "Backup incomplete: {Written}/{Expected} saved, {FailedDashboards} dashboard(s) and {FailedFolders} folder(s) skipped. Archive: '{BackupFile}'.",
                written, discovered, failedDashboards.Count, failedFolders.Count, backupFilePath);
        }

        return result;
    }

    private static string? BuildNote(int discovered, int failedDashboards, int failedFolders)
    {
        if (failedFolders > 0 || failedDashboards > 0)
            return $"Backup incomplete: {failedDashboards} dashboard(s) and {failedFolders} folder(s) could not be read.";

        return discovered == 0 ? "No dashboards found in the selected folders." : null;
    }

    private sealed class BackupManifest
    {
        public int Expected { get; set; }
        public int Written { get; set; }
        public IReadOnlyList<string> FailedIds { get; set; } = [];
        public IReadOnlyList<string> FailedFolders { get; set; } = [];
        public string? Note { get; set; }
    }

    private static string Sanitize(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(InvalidPathChars, chars[i]) >= 0 || chars[i] == '/')
                chars[i] = '_';
        }
        return new string(chars).Trim();
    }
}
