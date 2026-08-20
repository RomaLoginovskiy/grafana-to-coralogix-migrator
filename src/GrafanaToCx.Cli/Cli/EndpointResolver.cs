using GrafanaToCx.Core.Migration;

namespace GrafanaToCx.Cli.Cli;

/// <summary>
/// The single precedence chain deciding which Coralogix tenant a command targets, shared by
/// <c>import</c>, <c>verify</c>, <c>migrate</c> and <c>grafana-import</c>.
/// </summary>
/// <remarks>
/// <para>
/// Order: <c>--endpoint</c>, <c>--region</c>, the settings endpoint, the interactive prompt, the settings
/// region, error.
/// </para>
/// <para>
/// The prompt deliberately outranks the settings region, which is the opposite of the API-key policy in
/// <c>AppRunner.ResolveCxApiKey</c>. Do not "harmonise" the two: the shipped migration-settings.json always
/// names a region, so a prompt that only fired when settings were silent would never fire at all for anyone
/// passing <c>-s</c> — which is how <c>import --interactive</c> came to publish to eu1 without ever asking.
/// A key is one secret per tenant and has no flag because it would leak into shell history; a region is a
/// destination that legitimately changes between runs and leaks nothing.
/// </para>
/// <para>
/// There is deliberately no built-in fallback region. Guessing would publish into a tenant the operator
/// never named, and <c>import</c> creates folders and overwrites same-named dashboards, so a wrong-region
/// run is cleaned up by hand in someone else's account.
/// </para>
/// </remarks>
public static class EndpointResolver
{
    /// <summary>Which URL shape the chosen region resolves to.</summary>
    public enum Surface
    {
        /// <summary>Coralogix REST API — <c>{host}/mgmt/openapi/latest</c>.</summary>
        CoralogixRest,

        /// <summary>Coralogix-hosted Grafana — <c>{host}/grafana</c>.</summary>
        HostedGrafana
    }

    /// <summary>Which link of the chain produced the endpoint. Reported to the operator as provenance.</summary>
    public enum Source
    {
        EndpointFlag,
        RegionFlag,
        Prompt,
        SettingsEndpoint,
        SettingsRegion,
        Unresolved
    }

    /// <param name="SettingsEndpoint">
    /// Always null for <see cref="Surface.CoralogixRest"/> — the <c>coralogix</c> settings section has no
    /// endpoint key. Present so <c>grafanaImport.endpoint</c> keeps the precedence it has today.
    /// </param>
    /// <param name="SettingsRegion">
    /// Read from the raw configuration section, never from a bound <c>MigrationSettings</c>: a bound object
    /// cannot tell an absent key from a defaulted one, and its default is eu2 — a different tenant from the
    /// eu1 that the removed hardcoded endpoint used to select.
    /// </param>
    /// <param name="SettingsFileForMessages">Named in error text so the operator knows which file to edit.</param>
    public sealed record Inputs(
        string? EndpointFlag,
        string? RegionFlag,
        string? SettingsEndpoint,
        string? SettingsRegion,
        bool Interactive,
        string SettingsFileForMessages);

    /// <param name="Region">
    /// Canonical region the endpoint came from, or null when an explicit endpoint bypassed region resolution
    /// — there is no reverse map from a URL back to a region.
    /// </param>
    /// <param name="Error">Message for the caller to print. Non-null exactly when <see cref="Endpoint"/> is null.</param>
    public sealed record Result(string? Endpoint, string? Region, Source From, string? Error)
    {
        public bool Ok => Endpoint is not null;
    }

    /// <param name="promptRegion">
    /// Takes the seed region (may be null when the settings file names nothing usable) and returns the
    /// operator's choice, or null when they declined or the terminal could not render a picker.
    /// </param>
    public static Result Resolve(Inputs inputs, Surface surface, Func<string?, string?> promptRegion)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(promptRegion);

        if (Trimmed(inputs.EndpointFlag) is { } endpointFlag)
            return new Result(endpointFlag, Region: null, Source.EndpointFlag, Error: null);

        // A typo'd flag is an error even under --interactive: a script that names a bad region must fail
        // rather than get quietly redirected to whatever a picker happens to highlight.
        if (Trimmed(inputs.RegionFlag) is { } regionFlag)
        {
            return RegionMapper.Normalize(regionFlag) is { } normalized
                ? Resolved(normalized, Source.RegionFlag, surface)
                : Unresolved($"Unknown region '{regionFlag}'. Valid regions: {ValidRegions}.");
        }

        // Outranks the prompt, unlike the settings *region*. A configured URL is as explicit a target as
        // --endpoint is, it may name a host no region maps to, and there is nothing in it to seed a picker
        // with — so asking for a region here would silently redirect the operator somewhere else entirely.
        if (Trimmed(inputs.SettingsEndpoint) is { } settingsEndpoint)
            return new Result(settingsEndpoint, Region: null, Source.SettingsEndpoint, Error: null);

        // Unlike the flag, an unusable value here is not fatal — the operator is about to be asked anyway,
        // so seed the picker with nothing rather than refusing to start.
        var settingsRegion = Trimmed(inputs.SettingsRegion);
        var seed = settingsRegion is null ? null : RegionMapper.Normalize(settingsRegion);

        if (inputs.Interactive)
        {
            var chosen = Trimmed(promptRegion(seed));
            if (chosen is null)
                return Unresolved("No region selected.");

            return RegionMapper.Normalize(chosen) is { } normalized
                ? Resolved(normalized, Source.Prompt, surface)
                : Unresolved($"Unknown region '{chosen}'. Valid regions: {ValidRegions}.");
        }

        if (settingsRegion is not null)
        {
            return seed is { } normalized
                ? Resolved(normalized, Source.SettingsRegion, surface)
                : Unresolved(
                    $"Unknown region '{settingsRegion}' in '{inputs.SettingsFileForMessages}'. " +
                    $"Valid regions: {ValidRegions}.");
        }

        return Unresolved(
            $"No Coralogix target. Pass --endpoint or --region, or set a region in " +
            $"'{inputs.SettingsFileForMessages}'.");
    }

    private static string ValidRegions => string.Join(", ", RegionMapper.KnownRegions);

    /// <remarks>
    /// <paramref name="region"/> is always already normalized, so <see cref="RegionMapper"/> cannot throw
    /// here and this needs no try/catch.
    /// </remarks>
    private static Result Resolved(string region, Source from, Surface surface)
    {
        var endpoint = surface == Surface.HostedGrafana
            ? RegionMapper.ResolveGrafana(region)
            : RegionMapper.Resolve(region);

        return new Result(endpoint, region, from, Error: null);
    }

    private static Result Unresolved(string error) =>
        new(Endpoint: null, Region: null, Source.Unresolved, error);

    /// <summary>
    /// Collapses blank to null and trims. <see cref="RegionMapper.Normalize"/> deliberately does not trim
    /// (a padded value is a typo in code), but padding on a CLI argument or a JSON string is the CLI's
    /// problem to absorb.
    /// </summary>
    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
