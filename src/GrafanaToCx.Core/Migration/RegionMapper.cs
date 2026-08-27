namespace GrafanaToCx.Core.Migration;

public static class RegionMapper
{
    /// <summary>
    /// Every region this tool can resolve, in the order pickers should show them. Declared as an ordered
    /// list rather than read back from <see cref="RegionBaseUrls"/>, whose enumeration order is an
    /// implementation detail of <see cref="Dictionary{TKey,TValue}"/> and not part of its contract.
    /// </summary>
    public static IReadOnlyList<string> KnownRegions { get; } =
        ["eu1", "eu2", "us1", "us2", "ap1", "ap2", "ap3", "in1"];

    private static readonly Dictionary<string, string> RegionBaseUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eu1"] = "https://api.coralogix.com",
        ["eu2"] = "https://api.eu2.coralogix.com",
        ["us1"] = "https://api.coralogix.us",
        ["us2"] = "https://api.us2.coralogix.com",
        ["ap1"] = "https://api.ap1.coralogix.com",
        ["ap2"] = "https://api.ap2.coralogix.com",
        ["ap3"] = "https://api.ap3.coralogix.com",
        ["in1"] = "https://api.app.coralogix.in",
    };

    /// <summary>
    /// Returns the canonical spelling of <paramref name="region"/>, or null when it is not a known region.
    /// </summary>
    /// <remarks>
    /// Lookup is case-insensitive but the result is not: a picker seeded with "EU1" would fail to match its
    /// own "eu1" entry and silently show no default.
    /// </remarks>
    public static string? Normalize(string? region) =>
        region is null
            ? null
            : KnownRegions.FirstOrDefault(known => string.Equals(known, region, StringComparison.OrdinalIgnoreCase));

    private static string GetBaseUrl(string region)
    {
        if (RegionBaseUrls.TryGetValue(region, out var url))
            return url;

        throw new ArgumentException(
            $"Unknown Coralogix region '{region}'. Valid regions: {string.Join(", ", RegionBaseUrls.Keys)}");
    }

    /// <summary>
    /// Returns the Coralogix REST API base URL for the given region.
    /// Example: eu1 → https://api.coralogix.com/mgmt/openapi/latest
    /// </summary>
    public static string Resolve(string region) => $"{GetBaseUrl(region)}/mgmt/openapi/latest";

    /// <summary>
    /// Returns the embedded Grafana API base URL for the given region.
    /// Example: eu1 → https://api.coralogix.com/grafana
    /// </summary>
    public static string ResolveGrafana(string region) => $"{GetBaseUrl(region)}/grafana";
}
