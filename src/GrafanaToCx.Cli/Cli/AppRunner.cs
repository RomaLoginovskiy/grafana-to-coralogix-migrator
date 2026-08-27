using GrafanaToCx.Core.Migration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Sinks.OpenTelemetry;

namespace GrafanaToCx.Cli.Cli;

/// <summary>
/// Bootstrap and command dispatch. Parses args and routes to CommandHandlers or interactive mode.
/// </summary>
public static class AppRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parsed = ArgumentParser.Parse(args);
        using var bootstrapLoggerFactory = CreateBootstrapLoggerFactory();
        using var runtimeLoggerFactory = TryCreateRuntimeLoggerFactory(parsed);
        var loggerFactory = runtimeLoggerFactory ?? bootstrapLoggerFactory;
        var handlers = new CommandHandlers(loggerFactory);

        try
        {
            return parsed.Command switch
            {
                CommandKind.Interactive => await RunInteractiveFromArgs(handlers, parsed),
                CommandKind.Convert => await RunConvertFromArgs(handlers, parsed),
                CommandKind.Migrate => await RunMigrateFromArgs(handlers, parsed),
                CommandKind.Backup => await RunBackupFromArgs(handlers, parsed),
                CommandKind.Verify => await RunVerifyFromArgs(handlers, parsed),
                CommandKind.Import => await RunImportFromArgs(handlers, parsed),
                CommandKind.GrafanaImport => await RunGrafanaImportFromArgs(handlers, parsed),
                _ => await RunInteractiveFromArgs(handlers, parsed)
            };
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static ILoggerFactory CreateBootstrapLoggerFactory()
    {
        return LoggerFactory.Create(builder =>
            builder.ClearProviders()
                .AddConsole()
                .SetMinimumLevel(LogLevel.Warning));
    }

    private static ILoggerFactory? TryCreateRuntimeLoggerFactory(ParsedArgs parsed)
    {
        var settingsPath = ResolveSettingsPath(parsed);

        try
        {
            if (!File.Exists(settingsPath))
            {
                WarnLoggingFallback($"settings file '{settingsPath}' was not found");
                return null;
            }

            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile(settingsPath, optional: false, reloadOnChange: false)
                .Build();

            var serilogSection = configuration.GetSection("Serilog");
            if (!serilogSection.Exists())
            {
                WarnLoggingFallback("Serilog section is missing");
                return null;
            }

            var runtimeLogger = CreateRuntimeLoggerFromSection(serilogSection);

            return LoggerFactory.Create(builder =>
                builder.ClearProviders()
                    .AddSerilog(runtimeLogger, dispose: true));
        }
        catch (Exception ex)
        {
            WarnLoggingFallback(ex.Message);
            return null;
        }
    }

    private static string ResolveSettingsPath(ParsedArgs parsed)
    {
        if (parsed.Command is CommandKind.Migrate or CommandKind.Backup
            && !string.IsNullOrWhiteSpace(parsed.Get("settings")))
        {
            return parsed.Get("settings")!;
        }

        return "migration-settings.json";
    }

    private static Serilog.ILogger CreateRuntimeLoggerFromSection(IConfigurationSection serilogSection)
    {
        var minimumLevel = ParseMinimumLevel(serilogSection["MinimumLevel"]);
        var serviceName = serilogSection.GetSection("Properties")["Service"] ?? "grafana-to-cx-cli";

        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", serviceName);

        var hasSink = false;

        if (serilogSection.GetValue<bool?>("WriteTo:File:Enabled") ?? true)
        {
            hasSink = TryAddFileSink(loggerConfiguration, serilogSection.GetSection("WriteTo").GetSection("File")) || hasSink;
        }

        if (serilogSection.GetValue<bool?>("WriteTo:Otlp:Enabled") ?? true)
        {
            hasSink = TryAddOtlpSink(loggerConfiguration, serilogSection.GetSection("WriteTo").GetSection("Otlp"), serviceName) || hasSink;
        }

        if (!hasSink)
        {
            throw new InvalidOperationException("No Serilog sinks are enabled after applying configuration.");
        }

        return loggerConfiguration.CreateLogger();
    }

    private static bool TryAddFileSink(LoggerConfiguration loggerConfiguration, IConfigurationSection fileSection)
    {
        try
        {
            var path = fileSection["Path"] ?? "logs/grafana-to-cx-.json";
            var rollingInterval = ParseRollingInterval(fileSection["RollingInterval"]);
            var formatter = string.Equals(fileSection["Formatter"], "Json", StringComparison.OrdinalIgnoreCase)
                ? new JsonFormatter()
                : new JsonFormatter();

            loggerConfiguration.WriteTo.File(
                formatter,
                path,
                rollingInterval: rollingInterval);

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Serilog file sink initialization failed ({ex.Message}). Continuing without file sink.");
            return false;
        }
    }

    private static bool TryAddOtlpSink(LoggerConfiguration loggerConfiguration, IConfigurationSection otlpSection, string serviceName)
    {
        try
        {
            var endpoint = otlpSection["Endpoint"] ?? "http://localhost:4317";
            var protocol = ParseOtlpProtocol(otlpSection["Protocol"]);

            loggerConfiguration.WriteTo.OpenTelemetry(options =>
            {
                options.Endpoint = endpoint;
                options.Protocol = protocol;
                options.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = serviceName
                };
            });

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Serilog OTLP sink initialization failed ({ex.Message}). Continuing without OTLP sink.");
            return false;
        }
    }

    private static LogEventLevel ParseMinimumLevel(string? value)
    {
        return Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level)
            ? level
            : LogEventLevel.Information;
    }

    private static RollingInterval ParseRollingInterval(string? value)
    {
        return Enum.TryParse<RollingInterval>(value, ignoreCase: true, out var interval)
            ? interval
            : RollingInterval.Day;
    }

    private static OtlpProtocol ParseOtlpProtocol(string? value)
    {
        return Enum.TryParse<OtlpProtocol>(value, ignoreCase: true, out var protocol)
            ? protocol
            : OtlpProtocol.Grpc;
    }

    private static void WarnLoggingFallback(string reason)
    {
        Console.Error.WriteLine($"Warning: Serilog initialization failed ({reason}). Falling back to bootstrap console logging.");
    }

    /// <summary>
    /// Resolves which stored session the console should continue, then hands it to the menu loop.
    /// </summary>
    /// <remarks>
    /// An id that names nothing, or names more than one session, is a hard error rather than a quiet new
    /// session. An operator who typed <c>--resume</c> is asking for specific remembered answers; starting
    /// fresh instead would present hardcoded defaults that look like remembered ones, and the first thing
    /// they would accept by pressing Enter is a root directory or a dry-run flag they never chose.
    /// </remarks>
    private static async Task<int> RunInteractiveFromArgs(CommandHandlers handlers, ParsedArgs parsed)
    {
        var store = new SessionStore();
        var resume = parsed.Get("resume");
        InteractiveSession session;
        var resumed = false;

        if (!string.IsNullOrWhiteSpace(resume))
        {
            var found = store.Resolve(resume, out var ambiguous);
            if (found is null)
            {
                Console.Error.WriteLine(ambiguous.Count > 0
                    ? $"Error: session id '{resume}' is ambiguous — it matches {string.Join(", ", ambiguous)}. " +
                      "Supply more characters."
                    : $"Error: no stored session '{resume}'. Run with --resume (no id) to list sessions.");
                return 1;
            }

            session = found;
            resumed = true;
        }
        else if (parsed.GetBool("continue"))
        {
            var recent = store.MostRecent();
            if (recent is null)
            {
                Console.Error.WriteLine("Error: no stored sessions to continue.");
                return 1;
            }

            session = recent;
            resumed = true;
        }
        else if (resume is not null)
        {
            // "--resume" with no id: pick from the list.
            var sessions = store.List();
            if (sessions.Count == 0)
            {
                Console.Error.WriteLine("Error: no stored sessions to resume.");
                return 1;
            }

            var chosen = SelectOneWithFallback.SelectOne(
                "Resume which session?", sessions, DescribeSession);

            if (chosen is null)
            {
                Console.Error.WriteLine("No session selected.");
                return 1;
            }

            session = chosen;
            resumed = true;
        }
        else
        {
            session = store.Create();
        }

        return await handlers.RunInteractiveConsoleAsync(
            session.SettingsFile ?? "migration-settings.json", store, session, resumed);
    }

    /// <summary>
    /// One line per session in the resume picker. Leads with what the operator actually recognises — when
    /// they last used it and what they were pointing at — because the id alone identifies nothing to a
    /// human choosing between several.
    /// </summary>
    private static string DescribeSession(InteractiveSession session)
    {
        var target = session.GrafanaImportRootDir ?? session.ImportRootDir ?? session.ConvertInput;
        var parts = new List<string> { $"{session.Id}  {session.LastUsedAt.LocalDateTime:yyyy-MM-dd HH:mm}" };

        if (!string.IsNullOrWhiteSpace(session.Region)) parts.Add(session.Region);
        if (!string.IsNullOrWhiteSpace(target)) parts.Add(target);

        return string.Join("  ·  ", parts);
    }

    private static async Task<int> RunConvertFromArgs(CommandHandlers handlers, ParsedArgs parsed)
    {
        var input = parsed.Get("input");
        if (string.IsNullOrEmpty(input))
        {
            Console.Error.WriteLine("Error: convert requires an input file or directory.");
            return 1;
        }
        return await handlers.RunConvertAsync(input, parsed.Get("output"));
    }

    /// <remarks>
    /// An interactive migrate is handled by the menu, which already prompts for a region at startup, so it
    /// delegates untouched. A non-interactive run only overrides the endpoint when a flag named one:
    /// <see cref="CommandHandlers.RunMigrateAsync"/> refuses to run without a settings file, so an absent
    /// region there is config the operator wrote and can read — not an invisible default filling a void,
    /// which is why this keeps its existing bound-object fallback instead of the resolver's strictness.
    /// </remarks>
    private static async Task<int> RunMigrateFromArgs(CommandHandlers handlers, ParsedArgs parsed)
    {
        var settings = parsed.Get("settings") ?? "migration-settings.json";
        var interactive = parsed.GetBool("interactive");

        if (interactive)
            return await handlers.RunMigrateAsync(settings, interactive: true);

        string? cxEndpoint = null;
        if (parsed.Get("endpoint") is { Length: > 0 } || parsed.Get("region") is { Length: > 0 })
        {
            cxEndpoint = ResolveCoralogixEndpoint(parsed, settings, interactive: false);
            if (cxEndpoint is null) return 1;
        }

        return await handlers.RunMigrateAsync(settings, interactive: false, cxEndpoint);
    }

    private static async Task<int> RunBackupFromArgs(CommandHandlers handlers, ParsedArgs parsed)
    {
        var settings = parsed.Get("settings") ?? "migration-settings.json";
        return await handlers.RunBackupAsync(
            settings,
            parsed.Get("output"),
            parsed.Get("region"),
            parsed.GetBool("interactive"));
    }

    private static async Task<int> RunVerifyFromArgs(CommandHandlers handlers, ParsedArgs parsed)
    {
        var input = parsed.Get("input");
        if (string.IsNullOrEmpty(input))
        {
            Console.Error.WriteLine("Error: verify requires an input file.");
            return 1;
        }

        var settingsFile = parsed.Get("settings") ?? "migration-settings.json";
        var dashboardId = parsed.Get("dashboard-id");

        string? endpoint = null;
        if (!string.IsNullOrWhiteSpace(dashboardId))
        {
            endpoint = ResolveCoralogixEndpoint(parsed, settingsFile, parsed.GetBool("interactive"));
            if (endpoint is null) return 1;
        }

        return await handlers.RunVerifyAsync(input, endpoint, dashboardId);
    }

    /// <remarks>
    /// Target before credential, matching the interactive session prompt order: a run that cannot work out
    /// where to publish should say so before making the operator type a secret.
    /// </remarks>
    private static async Task<int> RunImportFromArgs(CommandHandlers handlers, ParsedArgs parsed)
    {
        var input = parsed.Get("input");
        if (string.IsNullOrWhiteSpace(input))
        {
            Console.Error.WriteLine("Error: import requires an input directory.");
            return 1;
        }

        var settingsFile = parsed.Get("settings") ?? "migration-settings.json";
        var interactive = parsed.GetBool("interactive");

        var endpoint = ResolveCoralogixEndpoint(parsed, settingsFile, interactive);
        if (endpoint is null) return 1;

        var cxApiKey = ResolveCxApiKey(settingsFile, interactive);
        if (cxApiKey is null) return 1;

        return await handlers.RunImportAsync(
            input, endpoint, cxApiKey, interactive, settingsFile);
    }

    private static async Task<int> RunGrafanaImportFromArgs(CommandHandlers handlers, ParsedArgs parsed)
    {
        var input = parsed.Get("input");
        var interactive = parsed.GetBool("interactive");

        if (string.IsNullOrWhiteSpace(input) && !interactive)
        {
            Console.Error.WriteLine("Error: grafana-import requires an input directory.");
            return 1;
        }

        var settingsFile = parsed.Get("settings") ?? "migration-settings.json";

        var endpoint = ResolveGrafanaEndpoint(parsed, settingsFile, interactive);
        if (endpoint is null) return 1;

        // The Coralogix key, not a Grafana one: the hosted Grafana sits behind the Coralogix gateway and
        // authenticates with the same bearer token.
        var cxApiKey = ResolveCxApiKey(settingsFile, interactive);
        if (cxApiKey is null) return 1;

        return await handlers.RunGrafanaImportAsync(
            input,
            endpoint,
            cxApiKey,
            interactive,
            settingsFile,
            overwriteOverride: parsed.GetBoolOrNull("overwrite"),
            dryRun: parsed.GetBool("dry-run"),
            recursiveOverride: parsed.GetBoolOrNull("recursive"));
    }

    /// <summary>
    /// Resolves the Coralogix-hosted Grafana target. Precedence: --endpoint, --region, grafanaImport.endpoint,
    /// the interactive picker, grafanaImport.region, coralogix.region.
    /// </summary>
    private static string? ResolveGrafanaEndpoint(ParsedArgs parsed, string settingsFile, bool interactive)
    {
        var (settingsEndpoint, settingsRegion) =
            ReadTargetFromSettings(settingsFile, "grafanaImport", fallBackToCoralogixRegion: true);

        var result = EndpointResolver.Resolve(
            new EndpointResolver.Inputs(
                parsed.Get("endpoint"), parsed.Get("region"),
                settingsEndpoint, settingsRegion,
                interactive, settingsFile),
            EndpointResolver.Surface.HostedGrafana,
            seed => PromptInput.PromptRegion("Coralogix region for the target Grafana", seed));

        return ReportTarget(result, "Grafana");
    }

    /// <inheritdoc cref="ResolveGrafanaEndpoint"/>
    /// <summary>
    /// Resolves the Coralogix REST API target. Precedence: --endpoint, --region, the interactive picker,
    /// coralogix.region.
    /// </summary>
    private static string? ResolveCoralogixEndpoint(ParsedArgs parsed, string settingsFile, bool interactive)
    {
        var (_, settingsRegion) =
            ReadTargetFromSettings(settingsFile, "coralogix", fallBackToCoralogixRegion: false);

        var result = EndpointResolver.Resolve(
            new EndpointResolver.Inputs(
                parsed.Get("endpoint"), parsed.Get("region"),
                SettingsEndpoint: null, settingsRegion,
                interactive, settingsFile),
            EndpointResolver.Surface.CoralogixRest,
            seed => PromptInput.PromptRegion("Coralogix region", seed));

        return ReportTarget(result, "Coralogix");
    }

    /// <summary>
    /// Prints the resolved target and where it came from, or the reason resolution failed. The provenance
    /// matters: with nothing printed, a command silently publishing into the wrong tenant looked identical
    /// to one publishing into the right one.
    /// </summary>
    private static string? ReportTarget(EndpointResolver.Result result, string label)
    {
        if (!result.Ok)
        {
            Console.Error.WriteLine($"Error: {result.Error}");
            return null;
        }

        Console.WriteLine($"{label} target: {result.Endpoint}  (from {Describe(result.From)})");
        return result.Endpoint;

        static string Describe(EndpointResolver.Source source) => source switch
        {
            EndpointResolver.Source.EndpointFlag => "--endpoint",
            EndpointResolver.Source.RegionFlag => "--region",
            EndpointResolver.Source.Prompt => "prompt",
            EndpointResolver.Source.SettingsEndpoint => "settings endpoint",
            EndpointResolver.Source.SettingsRegion => "settings region",
            _ => source.ToString()
        };
    }

    /// <param name="fallBackToCoralogixRegion">
    /// Set for the hosted-Grafana surface, where coralogix.region is the sensible last resort because that
    /// Grafana lives in the same tenant. The Coralogix surface reads coralogix.region directly instead.
    /// </param>
    /// <remarks>
    /// Reads the raw configuration section rather than binding <c>MigrationSettings</c>, which cannot
    /// distinguish an absent region from its own eu2 default — and silently choosing eu2 for someone who
    /// named nothing would be a different tenant from the eu1 the old hardcoded endpoint selected.
    /// </remarks>
    private static (string? Endpoint, string? Region) ReadTargetFromSettings(
        string settingsFile, string section, bool fallBackToCoralogixRegion)
    {
        if (!File.Exists(settingsFile)) return (null, null);

        try
        {
            var config = new ConfigurationBuilder()
                .AddJsonFile(Path.GetFullPath(settingsFile), optional: false)
                .Build();

            var target = config.GetSection(section);
            var region = target["region"];

            if (fallBackToCoralogixRegion && string.IsNullOrWhiteSpace(region))
                region = config.GetSection("coralogix")["region"];

            return (target["endpoint"], region);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Precedence: CX_API_KEY, credentials.cxApiKey, then an interactive prompt. Returns null once every
    /// source is exhausted, having already written the reason to stderr.
    /// </summary>
    /// <remarks>
    /// The prompt is the last resort rather than the first so that a configured key always wins: an
    /// unattended run and an interactive one should target the same tenant with the same credential.
    /// There is deliberately no flag for the key — that would leak it into shell history and the
    /// process list.
    /// </remarks>
    private static string? ResolveCxApiKey(string settingsFile, bool interactive)
    {
        var key = Environment.GetEnvironmentVariable("CX_API_KEY");
        if (!string.IsNullOrWhiteSpace(key)) return key;

        key = ReadCxApiKeyFromSettings(settingsFile);
        if (!string.IsNullOrWhiteSpace(key)) return key;

        if (interactive)
        {
            key = PromptInput.PromptPassword("Coralogix API key");
            if (!string.IsNullOrWhiteSpace(key)) return key;
        }

        Console.Error.WriteLine(
            "Error: no Coralogix API key. Set CX_API_KEY or credentials.cxApiKey in the settings file" +
            (interactive ? "." : ", or pass --interactive to be prompted."));
        return null;
    }

    private static string? ReadCxApiKeyFromSettings(string settingsFile)
    {
        if (!File.Exists(settingsFile)) return null;

        try
        {
            return new ConfigurationBuilder()
                .AddJsonFile(Path.GetFullPath(settingsFile), optional: false)
                .Build()
                .GetSection("credentials")["cxApiKey"];
        }
        catch
        {
            return null;
        }
    }
}
