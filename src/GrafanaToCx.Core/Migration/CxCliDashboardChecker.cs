using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Migration;

public sealed record CxCheckIssue(string Severity, string Location, string Message)
{
    public bool IsError => Severity.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
}

public sealed record CxCheckResult(bool Ran, IReadOnlyList<CxCheckIssue> Issues, string? SkipReason = null)
{
    public bool HasErrors => Issues.Any(i => i.IsError);
    public static CxCheckResult Skipped(string reason) => new(false, [], reason);
}

/// <summary>
/// Validates a converted dashboard against the live Coralogix API using the `cx` CLI's
/// read-only CheckDashboard endpoint, before anything is uploaded.
///
/// Entirely optional: if `cx` is not on PATH the check is skipped and migration proceeds
/// exactly as before. Nothing is persisted by the check itself.
/// </summary>
public sealed class CxCliDashboardChecker
{
    private static readonly Regex IssueRow = new(
        @"^\|\s*(?<severity>SEVERITY_\w+)\s*\|\s*(?<location>[^|]*?)\s*\|\s*(?<message>[^|]*?)\s*\|$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly ILogger _logger;
    private readonly string _apiKey;
    private readonly string _region;
    private readonly string? _profile;
    private readonly Lazy<bool> _isInstalled;

    /// <param name="profile">
    /// Optional cx profile. When set it is used instead of the API key, which is what an
    /// OAuth-authenticated profile needs — the migration's own key only works for
    /// key-authenticated accounts.
    /// </param>
    public CxCliDashboardChecker(ILogger logger, string apiKey, string region, string? profile = null)
    {
        _logger = logger;
        _apiKey = apiKey;
        _region = region;
        _profile = string.IsNullOrWhiteSpace(profile) ? null : profile;
        _isInstalled = new Lazy<bool>(DetectCli);
    }

    public bool IsInstalled => _isInstalled.Value;

    public async Task<CxCheckResult> CheckAsync(JObject dashboard, CancellationToken ct = default)
    {
        if (!IsInstalled)
            return CxCheckResult.Skipped("cx CLI is not installed");

        var path = Path.Combine(Path.GetTempPath(), $"cx-check-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, dashboard.ToString(Formatting.None), ct);

            string[] arguments = _profile is null
                ? ["dashboards", "check", "--from-file", path]
                : ["dashboards", "check", "--from-file", path, "--profile", _profile];

            var (exitCode, output) = await RunAsync(arguments, ct);

            // The CLI exits non-zero when it finds errors, so a non-zero code with no parsed
            // rows means the invocation itself failed — report that rather than a clean pass.
            var issues = ParseIssues(output);
            if (issues.Count == 0 && exitCode != 0)
            {
                _logger.LogWarning("cx check could not validate the dashboard: {Output}", Summarize(output));
                return CxCheckResult.Skipped($"cx check failed: {Summarize(output)}");
            }

            return new CxCheckResult(true, issues);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "cx check could not be run; continuing without validation.");
            return CxCheckResult.Skipped($"cx check errored: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
    }

    public static IReadOnlyList<CxCheckIssue> ParseIssues(string output)
    {
        var issues = new List<CxCheckIssue>();
        foreach (Match match in IssueRow.Matches(output))
        {
            var severity = match.Groups["severity"].Value;
            if (severity.Equals("SEVERITY", StringComparison.OrdinalIgnoreCase))
                continue;

            issues.Add(new CxCheckIssue(
                severity,
                match.Groups["location"].Value,
                match.Groups["message"].Value));
        }

        return issues;
    }

    private bool DetectCli()
    {
        try
        {
            var (exitCode, _) = RunAsync(["--version"], CancellationToken.None).GetAwaiter().GetResult();
            if (exitCode == 0)
                return true;

            _logger.LogInformation("cx CLI not usable (exit {Code}); pre-upload validation disabled.", exitCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("cx CLI not found ({Reason}); pre-upload validation disabled.", ex.Message);
            return false;
        }
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string[] arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cx",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        // With no profile the CLI takes credentials from the environment, reusing the
        // migration's own key and region rather than needing cx to be configured.
        if (_profile is null)
        {
            startInfo.Environment["CX_API_KEY"] = _apiKey;
            startInfo.Environment["CX_REGION"] = _region.ToUpperInvariant();
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stdout + "\n" + stderr);
    }

    private static string Summarize(string output)
    {
        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.Length > 0 && !l.StartsWith("[cx update]", StringComparison.Ordinal));

        return line is { Length: > 200 } ? line[..200] : line ?? "no output";
    }
}
