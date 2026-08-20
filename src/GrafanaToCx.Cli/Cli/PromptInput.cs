using GrafanaToCx.Core.Migration;
using Sharprompt;

namespace GrafanaToCx.Cli.Cli;

/// <summary>
/// Sharprompt-based input helpers for session config, region, and password.
/// </summary>
public static class PromptInput
{
    /// <summary>Used when neither the settings file nor the caller names a region.</summary>
    private const string FallbackRegion = "eu2";

    /// <param name="defaultRegion">
    /// Region to pre-select, normally <c>coralogix.region</c> from the settings file. Unknown or missing
    /// values fall back to <see cref="FallbackRegion"/> rather than failing the session.
    /// </param>
    public static SessionConfig? PromptSessionConfig(string? defaultRegion = null)
    {
        var region = PromptRegion("Coralogix region", defaultRegion);
        if (region is null)
        {
            Console.Error.WriteLine("No region selected.");
            return null;
        }

        // Every choice comes from RegionMapper.KnownRegions, so resolution cannot fail here.
        var cxEndpoint = RegionMapper.Resolve(region);

        var apiKey = Environment.GetEnvironmentVariable("CX_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = Prompt.Password("Coralogix API key", validators: [Validators.Required()]);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Console.Error.WriteLine("API key cannot be empty.");
                return null;
            }
        }
        else
        {
            Console.WriteLine("Using CX_API_KEY from environment.");
        }

        Console.WriteLine($"Connected to: {cxEndpoint}");
        return new SessionConfig(cxEndpoint, apiKey, GrafanaApiKey: null, Region: region);
    }

    public static string PromptPassword(string message)
    {
        return Prompt.Password(message, validators: [Validators.Required()]);
    }

    public static string? PromptPasswordOptional(string message)
    {
        return Prompt.Password(message);
    }

    /// <summary>
    /// Picks a region from <see cref="RegionMapper.KnownRegions"/>. Any answer is resolvable, so callers
    /// never have to handle a typo — the previous free-text prompt could abort a session on one.
    /// </summary>
    /// <param name="defaultValue">
    /// Pre-selected region. Unknown or missing values fall back to <see cref="FallbackRegion"/>.
    /// </param>
    /// <returns>The chosen region, or null when the operator declined or gave no valid answer.</returns>
    public static string? PromptRegion(string message, string? defaultValue = null)
    {
        var preselected = RegionMapper.Normalize(defaultValue) ?? FallbackRegion;
        return SelectOneWithFallback.SelectOne(
            message, RegionMapper.KnownRegions, region => region, preselected);
    }

    public static bool PromptConfirm(string message, bool defaultValue = false)
    {
        return Prompt.Confirm(message, defaultValue);
    }

    public static string AskInput(string message, string? defaultValue = null)
    {
        return defaultValue is null
            ? Prompt.Input<string>(message, validators: [Validators.Required()])
            : Prompt.Input<string>(message, defaultValue);
    }

    public static string? AskInputOptional(string message, string? defaultValue = null)
    {
        return Prompt.Input<string>(message, defaultValue ?? string.Empty);
    }

    public static T PromptSelect<T>(string message, IEnumerable<T> items, Func<T, string>? displaySelector = null) where T : notnull
    {
        return displaySelector is null
            ? Prompt.Select(message, items)
            : Prompt.Select(message, items, textSelector: displaySelector);
    }

    public static IReadOnlyList<T> PromptMultiSelect<T>(string message, IEnumerable<T> items, Func<T, string>? displaySelector = null) where T : notnull
    {
        var result = displaySelector is null
            ? Prompt.MultiSelect(message, items)
            : Prompt.MultiSelect(message, items, textSelector: displaySelector);
        return result.ToList();
    }
}
