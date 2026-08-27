using GrafanaToCx.Cli.Cli;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// The interactive console is the only command with no verb, so its flags have to be recognised from a
/// leading '-'. Before this existed, <c>--resume abc12345</c> fell through to the catch-all and started a
/// fresh session, discarding the id the operator had typed.
/// </summary>
public sealed class InteractiveArgumentParsingTests
{
    [Fact]
    public void Parse_NoArgs_IsInteractiveWithNoResume()
    {
        var parsed = ArgumentParser.Parse([]);

        Assert.Equal(CommandKind.Interactive, parsed.Command);
        Assert.Null(parsed.Get("resume"));
        Assert.False(parsed.GetBool("continue"));
    }

    [Fact]
    public void Parse_ResumeWithId_CarriesTheId()
    {
        var parsed = ArgumentParser.Parse(["--resume", "abc12345"]);

        Assert.Equal(CommandKind.Interactive, parsed.Command);
        Assert.Equal("abc12345", parsed.Get("resume"));
    }

    /// <summary>
    /// Empty rather than null: "--resume, no id" means show the picker, which is a different request from
    /// "--resume absent", and null cannot carry the difference.
    /// </summary>
    [Fact]
    public void Parse_BareResume_IsPresentButEmpty()
    {
        var parsed = ArgumentParser.Parse(["--resume"]);

        Assert.Equal(string.Empty, parsed.Get("resume"));
    }

    [Fact]
    public void Parse_BareResumeFollowedByAnotherFlag_DoesNotSwallowIt()
    {
        var parsed = ArgumentParser.Parse(["--resume", "--continue"]);

        Assert.Equal(string.Empty, parsed.Get("resume"));
        Assert.True(parsed.GetBool("continue"));
    }

    [Theory]
    [InlineData("--continue")]
    [InlineData("-c")]
    public void Parse_Continue_IsRecognisedInBothForms(string flag)
    {
        var parsed = ArgumentParser.Parse([flag]);

        Assert.Equal(CommandKind.Interactive, parsed.Command);
        Assert.True(parsed.GetBool("continue"));
        Assert.Null(parsed.Get("resume"));
    }

    [Fact]
    public void Parse_UnknownVerb_StillFallsBackToAFreshInteractiveSession()
    {
        var parsed = ArgumentParser.Parse(["wat"]);

        Assert.Equal(CommandKind.Interactive, parsed.Command);
        Assert.Null(parsed.Get("resume"));
        Assert.False(parsed.GetBool("continue"));
    }

    /// <summary>
    /// <c>-r</c> stays <c>--region</c> on the subcommands. One letter meaning region for a verb and resume
    /// without one is how an operator resumes a session while believing they named a tenant.
    /// </summary>
    [Fact]
    public void Parse_RegionFlagOnASubcommand_IsUnaffected()
    {
        var parsed = ArgumentParser.Parse(["grafana-import", "-r", "eu2"]);

        Assert.Equal(CommandKind.GrafanaImport, parsed.Command);
        Assert.Equal("eu2", parsed.Get("region"));
        Assert.Null(parsed.Get("resume"));
    }
}
