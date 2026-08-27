using GrafanaToCx.Cli.Cli;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// The region picker is the first prompt on four command paths, so a terminal that cannot render
/// Sharprompt's cursor UI must still be able to name a region rather than take a stack trace.
/// </summary>
public class SelectOneWithFallbackTests
{
    [Fact]
    public void TryParseNumericSelection_ValidInput_ReturnsTheOneBasedIndex()
    {
        var parsed = SelectOneWithFallback.TryParseNumericSelection(
            input: " 3 ", itemCount: 5, out var selectedIndex, out var validationError);

        Assert.True(parsed);
        Assert.Equal(3, selectedIndex);
        Assert.Equal(string.Empty, validationError);
    }

    [Theory]
    [InlineData("", "Select one option.")]
    [InlineData("   ", "Select one option.")]
    [InlineData(null, "Select one option.")]
    [InlineData("x", "'x' is not a valid number.")]
    [InlineData("0", "'0' is out of range. Enter a value between 1 and 3.")]
    [InlineData("4", "'4' is out of range. Enter a value between 1 and 3.")]
    public void TryParseNumericSelection_RejectsBadInput(string? input, string expectedError)
    {
        var parsed = SelectOneWithFallback.TryParseNumericSelection(
            input, itemCount: 3, out var selectedIndex, out var validationError);

        Assert.False(parsed);
        Assert.Equal(0, selectedIndex);
        Assert.Equal(expectedError, validationError);
    }

    [Fact]
    public void SelectOne_WhenSharpromptThrows_FallsBackToNumericSelection()
    {
        var inputs = new Queue<string?>(["x", "2"]);
        var logs = new List<string>();

        var selected = SelectOneWithFallback.SelectOne(
            "Coralogix region",
            ["eu1", "eu2", "us1"],
            x => x,
            preselected: "eu1",
            select: (_, _, _, _) => throw new ArgumentOutOfRangeException("top", "cursor issue"),
            readLine: () => inputs.Dequeue(),
            writeLine: logs.Add);

        Assert.Equal("eu2", selected);
        Assert.Contains(logs, l => l.Contains("Falling back to numeric selection.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, l => l.Contains("not a valid number", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, l => l.Contains("(current)", StringComparison.Ordinal));
    }

    /// <summary>
    /// Empty input almost always means stdin is closed. Taking the highlighted entry would publish to
    /// whatever the picker defaulted to — which, when the settings file names nothing, is a fallback region
    /// the operator never chose.
    /// </summary>
    [Fact]
    public void SelectOne_EmptyFallbackInput_DoesNotAcceptThePreselection()
    {
        var inputs = new Queue<string?>([null, null, null]);
        var logs = new List<string>();

        var selected = SelectOneWithFallback.SelectOne(
            "Coralogix region",
            ["eu1", "eu2"],
            x => x,
            preselected: "eu1",
            select: (_, _, _, _) => throw new InvalidOperationException("no cursor"),
            readLine: () => inputs.Dequeue(),
            writeLine: logs.Add);

        Assert.Null(selected);
        Assert.Contains(logs, l => l.Contains("No valid selection provided.", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectOne_WhenFallbackInputStaysInvalid_ReturnsNullAfterThreeAttempts()
    {
        var inputs = new Queue<string?>(["0", "9", "nope"]);
        var logs = new List<string>();

        var selected = SelectOneWithFallback.SelectOne(
            "Coralogix region",
            ["eu1", "eu2"],
            x => x,
            select: (_, _, _, _) => throw new InvalidOperationException("no cursor"),
            readLine: () => inputs.Dequeue(),
            writeLine: logs.Add);

        Assert.Null(selected);
        Assert.Empty(inputs);
    }

    [Fact]
    public void SelectOne_WhenPickerWorks_ReturnsItsAnswerAndNeverReadsStdin()
    {
        var selected = SelectOneWithFallback.SelectOne(
            "Coralogix region",
            ["eu1", "eu2"],
            x => x,
            preselected: "eu1",
            select: (_, items, _, _) => items[1],
            readLine: () => throw new InvalidOperationException("stdin must not be read"),
            writeLine: _ => { });

        Assert.Equal("eu2", selected);
    }

    [Fact]
    public void SelectOne_EmptyItemList_ReturnsNullWithoutPrompting() =>
        Assert.Null(SelectOneWithFallback.SelectOne(
            "Coralogix region",
            Array.Empty<string>(),
            x => x,
            select: (_, _, _, _) => throw new InvalidOperationException("must not prompt")));
}
