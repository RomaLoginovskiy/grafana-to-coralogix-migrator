using GrafanaToCx.Core.Migration;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Parsing of the `cx dashboards check` table output. The samples are real CLI output.
/// </summary>
public class CxCliDashboardCheckerTests
{
    private const string ValidOutput = """
        Checking dashboard from file...
        Reading dashboard definition from probe.json...
        Dashboard is valid (no issues)
        """;

    private const string ErrorOutput = """
        Checking dashboard from file...
        +----------------+---------------------------------------------------+-----------------------------+
        | Severity       | Location                                          | Message                     |
        +----------------+---------------------------------------------------+-----------------------------+
        | SEVERITY_ERROR | /layout/sections/0/rows/2/widgets/2/definition/pie | group_names cannot be empty |
        +----------------+---------------------------------------------------+-----------------------------+
        Error: dashboard check found 1 error(s) across profile(s); 0 warning(s) ignored
        """;

    private const string MixedOutput = """
        +------------------+---------------+-----------------------+
        | Severity         | Location      | Message               |
        +------------------+---------------+-----------------------+
        | SEVERITY_WARNING | /filters/0/id | filter id is required |
        +------------------+---------------+-----------------------+
        | SEVERITY_ERROR   | /layout       | something is wrong    |
        +------------------+---------------+-----------------------+
        """;

    [Fact]
    public void ValidOutput_YieldsNoIssues()
    {
        Assert.Empty(CxCliDashboardChecker.ParseIssues(ValidOutput));
    }

    [Fact]
    public void ErrorRow_IsParsed()
    {
        var issue = Assert.Single(CxCliDashboardChecker.ParseIssues(ErrorOutput));

        Assert.Equal("SEVERITY_ERROR", issue.Severity);
        Assert.Equal("/layout/sections/0/rows/2/widgets/2/definition/pie", issue.Location);
        Assert.Equal("group_names cannot be empty", issue.Message);
        Assert.True(issue.IsError);
    }

    [Fact]
    public void HeaderRow_IsNotMistakenForAnIssue()
    {
        Assert.DoesNotContain(CxCliDashboardChecker.ParseIssues(ErrorOutput),
            i => i.Severity.Equals("Severity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WarningsAndErrors_AreDistinguished()
    {
        var issues = CxCliDashboardChecker.ParseIssues(MixedOutput);

        Assert.Equal(2, issues.Count);
        Assert.Single(issues, i => i.IsError);
        Assert.Single(issues, i => !i.IsError);
    }

    [Fact]
    public void Result_HasErrors_OnlyWhenAnErrorIsPresent()
    {
        var warningOnly = new CxCheckResult(true, CxCliDashboardChecker.ParseIssues(MixedOutput)
            .Where(i => !i.IsError).ToList());
        var withError = new CxCheckResult(true, CxCliDashboardChecker.ParseIssues(ErrorOutput));

        Assert.False(warningOnly.HasErrors);
        Assert.True(withError.HasErrors);
    }

    [Fact]
    public void SkippedResult_DidNotRun()
    {
        var result = CxCheckResult.Skipped("cx CLI is not installed");

        Assert.False(result.Ran);
        Assert.False(result.HasErrors);
        Assert.Equal("cx CLI is not installed", result.SkipReason);
    }
}
