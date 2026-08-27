namespace GrafanaToCx.Cli.Cli;

/// <param name="Region">
/// Coralogix region the endpoint was resolved from. Carried alongside <paramref name="CxEndpoint"/> so the
/// settings menu and the Grafana-import prompt can seed their pickers with the current choice rather than a
/// hardcoded one — the endpoint alone would have to be reverse-mapped to recover it.
/// </param>
public sealed record SessionConfig(
    string CxEndpoint,
    string CxApiKey,
    string? GrafanaApiKey = null,
    string Region = "");
