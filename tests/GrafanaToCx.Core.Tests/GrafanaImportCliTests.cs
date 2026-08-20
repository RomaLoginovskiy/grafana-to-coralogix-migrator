using GrafanaToCx.Cli.Cli;
using GrafanaToCx.Core.ApiClient;
using GrafanaToCx.Core.Migration;
using Microsoft.Extensions.Configuration;

namespace GrafanaToCx.Core.Tests;

public class GrafanaImportArgumentParserTests
{
    [Theory]
    [InlineData("grafana-import")]
    [InlineData("g2g")]
    public void Parse_EitherVerb_SelectsTheGrafanaImportCommand(string verb) =>
        Assert.Equal(CommandKind.GrafanaImport, ArgumentParser.Parse([verb, "./dashboards"]).Command);

    [Fact]
    public void Parse_PositionalArgument_BecomesTheInputDirectory() =>
        Assert.Equal("./dashboards", ArgumentParser.Parse(["grafana-import", "./dashboards"]).Get("input"));

    [Theory]
    [InlineData("-e")]
    [InlineData("--endpoint")]
    public void Parse_EndpointFlag_IsCaptured(string flag) =>
        Assert.Equal("https://example/grafana",
            ArgumentParser.Parse(["grafana-import", "./d", flag, "https://example/grafana"]).Get("endpoint"));

    [Theory]
    [InlineData("-r")]
    [InlineData("--region")]
    public void Parse_RegionFlag_IsCaptured(string flag) =>
        Assert.Equal("eu2", ArgumentParser.Parse(["grafana-import", "./d", flag, "eu2"]).Get("region"));

    /// <summary>
    /// A default endpoint here would silently publish an eu2 tenant's dashboards into eu1, so the absence of
    /// one is the behaviour under test. <c>import</c> and <c>verify</c> once carried that eu1 default and
    /// have since been brought in line — see <see cref="TargetFlagParsingTests"/>.
    /// </summary>
    [Fact]
    public void Parse_NoTargetFlags_LeavesEndpointAndRegionUnset()
    {
        var parsed = ArgumentParser.Parse(["grafana-import", "./d"]);

        Assert.Null(parsed.Get("endpoint"));
        Assert.Null(parsed.Get("region"));
    }

    [Theory]
    [InlineData("-n")]
    [InlineData("--dry-run")]
    public void Parse_DryRunFlag_IsCaptured(string flag) =>
        Assert.True(ArgumentParser.Parse(["grafana-import", "./d", flag]).GetBool("dry-run"));

    [Fact]
    public void Parse_DryRunFlagAbsent_DefaultsToFalse() =>
        Assert.False(ArgumentParser.Parse(["grafana-import", "./d"]).GetBool("dry-run"));

    [Theory]
    [InlineData(new[] { "grafana-import", "./d", "--overwrite" }, true)]
    [InlineData(new[] { "grafana-import", "./d", "--no-overwrite" }, false)]
    [InlineData(new[] { "grafana-import", "./d" }, null)]
    public void Parse_OverwriteIsTriState(string[] args, bool? expected) =>
        Assert.Equal(expected, ArgumentParser.Parse(args).GetBoolOrNull("overwrite"));

    [Theory]
    [InlineData(new[] { "grafana-import", "./d", "--recursive" }, true)]
    [InlineData(new[] { "grafana-import", "./d", "-R" }, true)]
    [InlineData(new[] { "grafana-import", "./d", "--no-recursive" }, false)]
    [InlineData(new[] { "grafana-import", "./d" }, null)]
    public void Parse_RecursiveIsTriState(string[] args, bool? expected) =>
        Assert.Equal(expected, ArgumentParser.Parse(args).GetBoolOrNull("recursive"));

    [Fact]
    public void Parse_SettingsFlagAbsent_DefaultsToTheStandardFile() =>
        Assert.Equal("migration-settings.json", ArgumentParser.Parse(["grafana-import", "./d"]).Get("settings"));

    [Fact]
    public void Parse_InteractiveFlag_IsCaptured() =>
        Assert.True(ArgumentParser.Parse(["grafana-import", "./d", "-I"]).GetBool("interactive"));
}

public class GrafanaImportSettingsTests
{
    [Fact]
    public void Bind_FullSection_ReadsEveryField()
    {
        var settings = Bind("""
        {
          "grafanaImport": {
            "region": "eu2",
            "endpoint": "https://example/grafana",
            "checkpointFile": "custom-checkpoint.json",
            "reportFile": "custom-report.txt",
            "maxRetries": 9,
            "initialRetryDelaySeconds": 7,
            "overwriteExisting": false,
            "dryRun": true,
            "message": "custom message",
            "allowTargetDefaultFallback": true,
            "datasourceUidMap": { "src-a": "dst-a" },
            "grouping": { "separator": "__", "segmentCount": 3, "recursive": false, "ungroupedFolderName": "Other" }
          }
        }
        """).GrafanaImport;

        Assert.Equal("eu2", settings.Region);
        Assert.Equal("https://example/grafana", settings.Endpoint);
        Assert.Equal("custom-checkpoint.json", settings.CheckpointFile);
        Assert.Equal("custom-report.txt", settings.ReportFile);
        Assert.Equal(9, settings.MaxRetries);
        Assert.Equal(7, settings.InitialRetryDelaySeconds);
        Assert.False(settings.OverwriteExisting);
        Assert.True(settings.DryRun);
        Assert.Equal("custom message", settings.Message);
        Assert.True(settings.AllowTargetDefaultFallback);
        Assert.Equal("dst-a", settings.DatasourceUidMap["src-a"]);
        Assert.Equal("__", settings.Grouping.Separator);
        Assert.Equal(3, settings.Grouping.SegmentCount);
        Assert.False(settings.Grouping.Recursive);
        Assert.Equal("Other", settings.Grouping.UngroupedFolderName);
    }

    /// <summary>
    /// Recursive defaults on, unlike the Coralogix import flow: real Grafana backup trees are one
    /// directory per team, so a top-level-only scan finds nothing at all.
    /// </summary>
    [Fact]
    public void Bind_SectionAbsent_UsesDefaultsIncludingRecursiveDiscovery()
    {
        var settings = Bind("{}").GrafanaImport;

        Assert.Equal("grafana-import-checkpoint.json", settings.CheckpointFile);
        Assert.Equal("grafana-import-report.txt", settings.ReportFile);
        Assert.True(settings.OverwriteExisting);
        Assert.False(settings.DryRun);
        Assert.False(settings.AllowTargetDefaultFallback);
        Assert.True(settings.Grouping.Recursive);
        Assert.Empty(settings.DatasourceUidMap);
    }

    /// <summary>The three flows must never share a checkpoint; the shipped defaults must already satisfy that.</summary>
    [Fact]
    public void Defaults_ForAllThreeFlows_DoNotCollide()
    {
        var settings = Bind("{}");

        ImportOrchestrator.GuardCheckpointPaths(
            settings.Migration.CheckpointFile,
            settings.Import.CheckpointFile,
            settings.GrafanaImport.CheckpointFile);
    }

    [Fact]
    public void ToImportSettings_CarriesEveryFieldAndHonoursOverrides()
    {
        var grafanaImport = Bind("""
        { "grafanaImport": { "checkpointFile": "c.json", "reportFile": "r.txt", "maxRetries": 4,
                             "initialRetryDelaySeconds": 3, "overwriteExisting": true } }
        """).GrafanaImport;

        var grouping = new FolderGroupingSettings { Separator = "|", SegmentCount = 1, Recursive = false };
        var result = grafanaImport.ToImportSettings(grouping, overwriteOverride: false);

        Assert.Equal("c.json", result.CheckpointFile);
        Assert.Equal("r.txt", result.ReportFile);
        Assert.Equal(4, result.MaxRetries);
        Assert.Equal(3, result.InitialRetryDelaySeconds);
        Assert.False(result.OverwriteExisting);
        Assert.Equal("|", result.Grouping.Separator);
    }

    [Fact]
    public void ToImportSettings_WithoutOverrides_KeepsTheConfiguredValues()
    {
        var grafanaImport = Bind("""{ "grafanaImport": { "overwriteExisting": true } }""").GrafanaImport;

        var result = grafanaImport.ToImportSettings();

        Assert.True(result.OverwriteExisting);
        Assert.True(result.Grouping.Recursive);
    }

    /// <summary>The shipped settings file is what a first run actually uses, so its section must bind.</summary>
    [Fact]
    public void ShippedSettingsFile_ContainsABindableGrafanaImportSection()
    {
        var path = FindRepositoryFile("src/GrafanaToCx.Cli/migration-settings.json");
        if (path is null) return;

        var settings = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build()
            .Get<MigrationSettings>()!;

        Assert.Equal("grafana-import-checkpoint.json", settings.GrafanaImport.CheckpointFile);
        Assert.True(settings.GrafanaImport.Grouping.Recursive);

        ImportOrchestrator.GuardCheckpointPaths(
            settings.Migration.CheckpointFile,
            settings.Import.CheckpointFile,
            settings.GrafanaImport.CheckpointFile);
    }

    private static MigrationSettings Bind(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"g2g-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        try
        {
            return new ConfigurationBuilder()
                .AddJsonFile(path, optional: false)
                .Build()
                .Get<MigrationSettings>() ?? new MigrationSettings();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string? FindRepositoryFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}

public class DryRunFolderTargetTests
{
    [Fact]
    public async Task GetOrCreateFolderAsync_ExistingFolder_ReturnsItsIdWithoutCreating()
    {
        var inner = new FakeFolderTarget { Folders = { new TargetFolder("uid-1", "Team") } };
        var sut = new DryRunFolderTarget(inner);

        Assert.Equal("uid-1", await sut.GetOrCreateFolderAsync("team"));
        Assert.Equal(0, inner.CreateCallCount);
    }

    /// <summary>
    /// Plan construction resolves folders, so a dry run that short-circuits afterwards has already created
    /// them. The placeholder keeps the plan complete and printable while writing nothing.
    /// </summary>
    [Fact]
    public async Task GetOrCreateFolderAsync_MissingFolder_ReturnsAPlaceholderWithoutCreating()
    {
        var inner = new FakeFolderTarget();
        var sut = new DryRunFolderTarget(inner);

        var id = await sut.GetOrCreateFolderAsync("New Team");

        Assert.Equal(DryRunFolderTarget.PendingPrefix + "New Team", id);
        Assert.True(DryRunFolderTarget.IsPending(id));
        Assert.Equal(0, inner.CreateCallCount);
    }

    [Fact]
    public void IsPending_RealFolderId_IsFalse() => Assert.False(DryRunFolderTarget.IsPending("uid-1"));

    [Fact]
    public void Decorator_PassesThroughTargetCapabilities()
    {
        var sut = new DryRunFolderTarget(new FakeFolderTarget());

        Assert.Equal("Fake", sut.TargetDisplayName);
        Assert.False(sut.SupportsNestedFolders);
    }

    private sealed class FakeFolderTarget : IDashboardFolderTarget
    {
        public List<TargetFolder> Folders { get; } = [];
        public int CreateCallCount { get; private set; }

        public string TargetDisplayName => "Fake";
        public bool SupportsNestedFolders => false;

        public Task<IReadOnlyList<TargetFolder>> ListFoldersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TargetFolder>>(Folders);

        public Task<string?> GetOrCreateFolderAsync(
            string name, string? parentId = null, CancellationToken ct = default)
        {
            CreateCallCount++;
            var folder = new TargetFolder($"created-{name}", name);
            Folders.Add(folder);
            return Task.FromResult<string?>(folder.Id);
        }
    }
}
