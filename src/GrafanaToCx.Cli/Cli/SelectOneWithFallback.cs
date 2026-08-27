using Sharprompt;

namespace GrafanaToCx.Cli.Cli;

/// <summary>
/// Single-choice counterpart to <see cref="MultiSelectWithFallback"/>: falls back to numeric entry when
/// Sharprompt's cursor-driven picker cannot run.
/// </summary>
/// <remarks>
/// The region picker is the first prompt on four command paths, so an unrenderable list would kill the
/// command with a Sharprompt stack trace before it did anything — and leave the operator no way forward
/// short of discovering the settings file. <see cref="MultiSelectWithFallback"/> exists because Sharprompt
/// does fail in real terminals; that failure at least happened mid-flow.
/// </remarks>
public static class SelectOneWithFallback
{
    private const int MaxAttempts = 3;

    /// <param name="preselected">Highlighted entry for the interactive picker, and shown as a hint in the fallback.</param>
    /// <returns>The chosen item, or null when the operator declined or gave no valid answer.</returns>
    public static T? SelectOne<T>(
        string message,
        IReadOnlyList<T> items,
        Func<T, string> textSelector,
        T? preselected = null,
        Func<string, IReadOnlyList<T>, Func<T, string>, T?, T?>? select = null,
        Func<string?>? readLine = null,
        Action<string>? writeLine = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(textSelector);

        if (items.Count == 0)
            return null;

        select ??= PromptWithSharprompt;
        readLine ??= Console.ReadLine;
        writeLine ??= Console.WriteLine;

        try
        {
            return select(message, items, textSelector, preselected);
        }
        catch (Exception)
        {
            writeLine("Interactive selection is unavailable in this terminal. Falling back to numeric selection.");
            return SelectUsingNumericFallback(message, items, textSelector, preselected, readLine, writeLine);
        }
    }

    public static bool TryParseNumericSelection(
        string? input,
        int itemCount,
        out int selectedIndex,
        out string validationError)
    {
        selectedIndex = 0;

        if (itemCount <= 0)
        {
            validationError = "No selectable items are available.";
            return false;
        }

        // Deliberately not "accept the preselection": an empty answer here usually means stdin is closed,
        // and silently taking the highlighted entry would publish to whatever the picker defaulted to.
        if (string.IsNullOrWhiteSpace(input))
        {
            validationError = "Select one option.";
            return false;
        }

        if (!int.TryParse(input.Trim(), out var index))
        {
            validationError = $"'{input.Trim()}' is not a valid number.";
            return false;
        }

        if (index < 1 || index > itemCount)
        {
            validationError = $"'{index}' is out of range. Enter a value between 1 and {itemCount}.";
            return false;
        }

        selectedIndex = index;
        validationError = string.Empty;
        return true;
    }

    private static T? PromptWithSharprompt<T>(
        string message,
        IReadOnlyList<T> items,
        Func<T, string> textSelector,
        T? preselected)
        where T : class
    {
        return preselected is null
            ? Prompt.Select(message, items, textSelector: textSelector)
            : Prompt.Select(message, items, defaultValue: preselected, textSelector: textSelector);
    }

    private static T? SelectUsingNumericFallback<T>(
        string message,
        IReadOnlyList<T> items,
        Func<T, string> textSelector,
        T? preselected,
        Func<string?> readLine,
        Action<string> writeLine)
        where T : class
    {
        writeLine($"{message} — select by number:");
        for (var i = 0; i < items.Count; i++)
        {
            var marker = preselected is not null && EqualityComparer<T>.Default.Equals(items[i], preselected)
                ? "  (current)"
                : string.Empty;
            writeLine($"  {i + 1}. {textSelector(items[i])}{marker}");
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            writeLine("Enter selection (example: 1):");
            var input = readLine();

            if (TryParseNumericSelection(input, items.Count, out var selectedIndex, out var validationError))
                return items[selectedIndex - 1];

            writeLine(validationError);
        }

        writeLine("No valid selection provided.");
        return null;
    }
}
