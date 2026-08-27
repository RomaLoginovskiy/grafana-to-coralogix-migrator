using GrafanaToCx.Cli.Cli;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Locks the precedence chain every command shares: --endpoint, --region, the settings endpoint, the
/// interactive picker, the settings region, error. The prompt is a spy so the whole matrix runs without a
/// terminal, and so tests can assert on whether it was reached at all.
/// </summary>
public class EndpointResolverTests
{
    private const string SettingsFile = "migration-settings.json";

    private sealed class PromptSpy
    {
        private readonly string? _answer;

        public PromptSpy(string? answer) => _answer = answer;

        public bool WasInvoked { get; private set; }
        public string? Seed { get; private set; }

        public string? Prompt(string? seed)
        {
            WasInvoked = true;
            Seed = seed;
            return _answer;
        }
    }

    private static EndpointResolver.Inputs Inputs(
        string? endpointFlag = null,
        string? regionFlag = null,
        string? settingsEndpoint = null,
        string? settingsRegion = null,
        bool interactive = false) =>
        new(endpointFlag, regionFlag, settingsEndpoint, settingsRegion, interactive, SettingsFile);

    [Fact]
    public void EndpointFlag_WinsOverEverythingIncludingInteractive()
    {
        var prompt = new PromptSpy("us1");

        var result = EndpointResolver.Resolve(
            Inputs("https://custom/api", regionFlag: "eu2", settingsEndpoint: "https://settings/api",
                settingsRegion: "ap1", interactive: true),
            EndpointResolver.Surface.CoralogixRest, prompt.Prompt);

        Assert.Equal("https://custom/api", result.Endpoint);
        Assert.Equal(EndpointResolver.Source.EndpointFlag, result.From);
        Assert.Null(result.Region);
        Assert.False(prompt.WasInvoked);
    }

    [Fact]
    public void RegionFlag_WinsOverPromptAndSettings()
    {
        var prompt = new PromptSpy("us1");

        var result = EndpointResolver.Resolve(
            Inputs(regionFlag: "eu2", settingsEndpoint: "https://settings/api", settingsRegion: "ap1",
                interactive: true),
            EndpointResolver.Surface.CoralogixRest, prompt.Prompt);

        Assert.Equal("https://api.eu2.coralogix.com/mgmt/openapi/latest", result.Endpoint);
        Assert.Equal("eu2", result.Region);
        Assert.Equal(EndpointResolver.Source.RegionFlag, result.From);
        Assert.False(prompt.WasInvoked);
    }

    /// <summary>
    /// A configured URL may name a host no region maps to, and there is nothing in it to seed a picker with,
    /// so asking for a region would redirect the operator somewhere they never named.
    /// </summary>
    [Fact]
    public void SettingsEndpoint_WinsOverThePrompt()
    {
        var prompt = new PromptSpy("us1");

        var result = EndpointResolver.Resolve(
            Inputs(settingsEndpoint: "https://my-grafana.internal", settingsRegion: "ap1", interactive: true),
            EndpointResolver.Surface.HostedGrafana, prompt.Prompt);

        Assert.Equal("https://my-grafana.internal", result.Endpoint);
        Assert.Equal(EndpointResolver.Source.SettingsEndpoint, result.From);
        Assert.False(prompt.WasInvoked);
    }

    [Fact]
    public void Interactive_SeedsThePromptWithTheSettingsRegion()
    {
        var prompt = new PromptSpy("eu2");

        EndpointResolver.Resolve(
            Inputs(settingsRegion: "ap1", interactive: true),
            EndpointResolver.Surface.CoralogixRest, prompt.Prompt);

        Assert.True(prompt.WasInvoked);
        Assert.Equal("ap1", prompt.Seed);
    }

    [Fact]
    public void Interactive_WithNoSettingsRegion_SeedsThePromptWithNothing()
    {
        var prompt = new PromptSpy("eu2");

        var result = EndpointResolver.Resolve(
            Inputs(interactive: true), EndpointResolver.Surface.CoralogixRest, prompt.Prompt);

        Assert.True(prompt.WasInvoked);
        Assert.Null(prompt.Seed);
        Assert.Equal("eu2", result.Region);
    }

    /// <summary>
    /// The decisive test for the chosen precedence. The shipped settings file always names a region, so a
    /// prompt that only fired when settings were silent would never fire — which is how
    /// `import --interactive` came to publish to eu1 without ever asking. If this test is ever "fixed" to
    /// make settings win, that bug is back.
    /// </summary>
    [Fact]
    public void Interactive_PromptAnswerBeatsTheSettingsRegion()
    {
        var prompt = new PromptSpy("eu2");

        var result = EndpointResolver.Resolve(
            Inputs(settingsRegion: "eu1", interactive: true),
            EndpointResolver.Surface.CoralogixRest, prompt.Prompt);

        Assert.Equal("eu2", result.Region);
        Assert.Equal("https://api.eu2.coralogix.com/mgmt/openapi/latest", result.Endpoint);
        Assert.Equal(EndpointResolver.Source.Prompt, result.From);
    }

    [Fact]
    public void NonInteractive_UsesTheSettingsRegionWithoutPrompting()
    {
        var prompt = new PromptSpy("us1");

        var result = EndpointResolver.Resolve(
            Inputs(settingsRegion: "eu1"), EndpointResolver.Surface.CoralogixRest, prompt.Prompt);

        Assert.Equal("https://api.coralogix.com/mgmt/openapi/latest", result.Endpoint);
        Assert.Equal("eu1", result.Region);
        Assert.Equal(EndpointResolver.Source.SettingsRegion, result.From);
        Assert.False(prompt.WasInvoked);
    }

    [Fact]
    public void NothingNamesATarget_FailsAndPointsAtBothRemedies()
    {
        var result = EndpointResolver.Resolve(
            Inputs(), EndpointResolver.Surface.CoralogixRest, _ => null);

        Assert.False(result.Ok);
        Assert.Equal(EndpointResolver.Source.Unresolved, result.From);
        Assert.Contains("--region", result.Error);
        Assert.Contains(SettingsFile, result.Error);
    }

    /// <summary>
    /// A script naming a bad region must fail rather than be quietly redirected to whatever a picker
    /// happens to highlight, so --interactive does not soften the flag.
    /// </summary>
    [Fact]
    public void UnknownRegionFlag_FailsWithoutPromptingEvenWhenInteractive()
    {
        var prompt = new PromptSpy("eu2");

        var result = EndpointResolver.Resolve(
            Inputs(regionFlag: "eu9", interactive: true),
            EndpointResolver.Surface.CoralogixRest, prompt.Prompt);

        Assert.False(result.Ok);
        Assert.Contains("eu9", result.Error);
        Assert.False(prompt.WasInvoked);
    }

    /// <summary>
    /// Unlike the flag, a stale value in the settings file is not fatal under --interactive: the operator is
    /// about to be asked anyway, so the picker opens with no default rather than the run refusing to start.
    /// </summary>
    [Fact]
    public void UnknownSettingsRegion_Interactive_PromptsWithNoSeed()
    {
        var prompt = new PromptSpy("eu2");

        var result = EndpointResolver.Resolve(
            Inputs(settingsRegion: "eu-west", interactive: true),
            EndpointResolver.Surface.CoralogixRest, prompt.Prompt);

        Assert.True(prompt.WasInvoked);
        Assert.Null(prompt.Seed);
        Assert.True(result.Ok);
        Assert.Equal("eu2", result.Region);
    }

    [Fact]
    public void UnknownSettingsRegion_NonInteractive_FailsNamingTheFileAndTheValue()
    {
        var result = EndpointResolver.Resolve(
            Inputs(settingsRegion: "eu-west"), EndpointResolver.Surface.CoralogixRest, _ => null);

        Assert.False(result.Ok);
        Assert.Contains("eu-west", result.Error);
        Assert.Contains(SettingsFile, result.Error);
    }

    [Fact]
    public void Surface_SelectsTheUrlSuffix()
    {
        var rest = EndpointResolver.Resolve(
            Inputs(regionFlag: "eu1"), EndpointResolver.Surface.CoralogixRest, _ => null);
        var grafana = EndpointResolver.Resolve(
            Inputs(regionFlag: "eu1"), EndpointResolver.Surface.HostedGrafana, _ => null);

        Assert.Equal("https://api.coralogix.com/mgmt/openapi/latest", rest.Endpoint);
        Assert.Equal("https://api.coralogix.com/grafana", grafana.Endpoint);
    }

    /// <summary>
    /// RegionMapper.Normalize deliberately rejects padded values, so the CLI absorbs padding itself —
    /// a region pasted from a script or a JSON string should not be a typo.
    /// </summary>
    [Theory]
    [InlineData("  eu1  ", null)]
    [InlineData(null, "  eu1  ")]
    public void PaddedRegion_IsAcceptedAndReportedCanonically(string? regionFlag, string? settingsRegion)
    {
        var result = EndpointResolver.Resolve(
            Inputs(regionFlag: regionFlag, settingsRegion: settingsRegion),
            EndpointResolver.Surface.CoralogixRest, _ => null);

        Assert.Equal("eu1", result.Region);
        Assert.Equal("https://api.coralogix.com/mgmt/openapi/latest", result.Endpoint);
    }

    /// <summary>
    /// Locks grafana-import's pre-existing chain so the move onto the shared resolver is provably
    /// behaviour-preserving for non-interactive runs.
    /// </summary>
    [Fact]
    public void SettingsEndpoint_BeatsSettingsRegionButLosesToTheRegionFlag()
    {
        var viaSettings = EndpointResolver.Resolve(
            Inputs(settingsEndpoint: "https://settings/grafana", settingsRegion: "eu2"),
            EndpointResolver.Surface.HostedGrafana, _ => null);

        var viaFlag = EndpointResolver.Resolve(
            Inputs(regionFlag: "eu2", settingsEndpoint: "https://settings/grafana"),
            EndpointResolver.Surface.HostedGrafana, _ => null);

        Assert.Equal("https://settings/grafana", viaSettings.Endpoint);
        Assert.Equal("https://api.eu2.coralogix.com/grafana", viaFlag.Endpoint);
    }

    /// <summary>
    /// A declined or unrenderable picker must not fall through to the settings region: the operator was
    /// asked precisely because the answer was theirs to give.
    /// </summary>
    [Fact]
    public void DeclinedPrompt_FailsRatherThanFallingBackToSettings()
    {
        var result = EndpointResolver.Resolve(
            Inputs(settingsRegion: "eu1", interactive: true),
            EndpointResolver.Surface.CoralogixRest, _ => null);

        Assert.False(result.Ok);
        Assert.Equal(EndpointResolver.Source.Unresolved, result.From);
        Assert.Equal("No region selected.", result.Error);
    }

    [Fact]
    public void Resolve_RejectsANullPrompt() =>
        Assert.Throws<ArgumentNullException>(() =>
            EndpointResolver.Resolve(Inputs(regionFlag: "eu1"), EndpointResolver.Surface.CoralogixRest, null!));
}

/// <summary>
/// Covers the target flags on the commands that used to hardcode an eu1 endpoint. The removed default is
/// asserted directly: reintroducing it would make every one of these commands publish into a tenant the
/// operator never named.
/// </summary>
public class TargetFlagParsingTests
{
    [Theory]
    [InlineData("import", "-r")]
    [InlineData("import", "--region")]
    [InlineData("verify", "-r")]
    [InlineData("verify", "--region")]
    [InlineData("migrate", "-r")]
    [InlineData("migrate", "--region")]
    public void Parse_RegionFlag_IsCaptured(string verb, string flag) =>
        Assert.Equal("us2", ArgumentParser.Parse([verb, "./d", flag, "us2"]).Get("region"));

    [Theory]
    [InlineData("import")]
    [InlineData("verify")]
    public void Parse_NoTargetFlags_LeavesEndpointAndRegionUnset(string verb)
    {
        var parsed = ArgumentParser.Parse([verb, "./d"]);

        Assert.Null(parsed.Get("endpoint"));
        Assert.Null(parsed.Get("region"));
    }

    [Theory]
    [InlineData("-s")]
    [InlineData("--settings")]
    public void ParseVerify_SettingsFlag_IsCaptured(string flag) =>
        Assert.Equal("custom.json", ArgumentParser.Parse(["verify", "./d.json", flag, "custom.json"]).Get("settings"));

    [Fact]
    public void ParseVerify_SettingsFlagAbsent_DefaultsToTheStandardFile() =>
        Assert.Equal("migration-settings.json", ArgumentParser.Parse(["verify", "./d.json"]).Get("settings"));

    [Theory]
    [InlineData("-I")]
    [InlineData("--interactive")]
    public void ParseVerify_InteractiveFlag_IsCaptured(string flag) =>
        Assert.True(ArgumentParser.Parse(["verify", "./d.json", flag]).GetBool("interactive"));

    [Fact]
    public void ParseVerify_InteractiveFlagAbsent_DefaultsToFalse() =>
        Assert.False(ArgumentParser.Parse(["verify", "./d.json"]).GetBool("interactive"));

    /// <summary>The flags added here must not have displaced the arguments the commands already took.</summary>
    [Fact]
    public void ParseVerify_KeepsItsExistingArguments()
    {
        var parsed = ArgumentParser.Parse(
            ["verify", "./d.json", "-d", "abc123", "-e", "https://custom/api"]);

        Assert.Equal("./d.json", parsed.Get("input"));
        Assert.Equal("abc123", parsed.Get("dashboard-id"));
        Assert.Equal("https://custom/api", parsed.Get("endpoint"));
    }

    [Fact]
    public void ParseMigrate_KeepsItsExistingArguments()
    {
        var parsed = ArgumentParser.Parse(["migrate", "-s", "custom.json", "-I"]);

        Assert.Equal("custom.json", parsed.Get("settings"));
        Assert.True(parsed.GetBool("interactive"));
    }
}
