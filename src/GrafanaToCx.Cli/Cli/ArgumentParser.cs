namespace GrafanaToCx.Cli.Cli;

/// <summary>
/// Parses command-style arguments for compatibility with existing CLI usage.
/// No System.CommandLine dependency.
/// </summary>
/// <remarks>
/// No command defaults its endpoint. The target is resolved later by <see cref="EndpointResolver"/> from
/// --endpoint, then --region, then the settings file, and the command fails if none of them says where to
/// publish. <c>import</c> and <c>verify</c> used to default to eu1 here, which meant an operator who never
/// named a region published into that tenant without being told — and <c>import</c> creates folders and
/// overwrites same-named dashboards, so the mistake is undone by hand in someone else's account.
/// </remarks>
public static class ArgumentParser
{
    public static ParsedArgs Parse(string[] args)
    {
        if (args.Length == 0)
            return ParseInteractive(ReadOnlySpan<string>.Empty);

        // Interactive takes flags but no verb, so a leading '-' must be parsed rather than falling through
        // to the catch-all below — which would start a fresh session and silently drop the --resume the
        // operator typed, losing the very state they asked to come back to.
        if (args[0].StartsWith('-'))
            return ParseInteractive(args.AsSpan());

        var cmd = args[0].ToLowerInvariant();
        var rest = args.AsSpan(1);

        return cmd switch
        {
            "convert" => ParseConvert(rest),
            "migrate" => ParseMigrate(rest),
            "assess" => ParseAssess(rest),
            "verify" => ParseVerify(rest),
            "import" => ParseImport(rest),
            "grafana-import" or "g2g" => ParseGrafanaImport(rest),
            _ => ParseInteractive(ReadOnlySpan<string>.Empty)
        };
    }

    /// <remarks>
    /// <c>-r</c> is deliberately not an alias for <c>--resume</c>: every subcommand already spells
    /// <c>--region</c> that way, and one letter meaning two things depending on the verb is how an
    /// operator ends up resuming a session when they meant to name a tenant.
    /// </remarks>
    private static ParsedArgs ParseInteractive(ReadOnlySpan<string> args)
    {
        string? resume = null;
        var cont = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--resume")
            {
                // Empty, not null: "--resume with no id" means show the picker, which is a different
                // request from "--resume absent", and null cannot express the difference.
                resume = string.Empty;

                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    resume = args[i + 1];
                    i++;
                }
            }
            else if (arg is "-c" or "--continue")
            {
                cont = true;
            }
        }

        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["resume"] = resume,
            ["continue"] = cont ? "true" : "false"
        };
        return new ParsedArgs(CommandKind.Interactive, dict);
    }

    private static ParsedArgs ParseConvert(ReadOnlySpan<string> rest)
    {
        string? input = null;
        string? output = null;

        for (var i = 0; i < rest.Length; i++)
        {
            var arg = rest[i];
            if (arg is "-o" or "--output")
            {
                if (i + 1 < rest.Length)
                {
                    output = rest[i + 1];
                    i++;
                }
            }
            else if (!arg.StartsWith('-'))
            {
                input ??= arg;
            }
        }

        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = input,
            ["output"] = output
        };
        return new ParsedArgs(CommandKind.Convert, dict);
    }

    private static ParsedArgs ParseMigrate(ReadOnlySpan<string> rest)
    {
        string? settings = "migration-settings.json";
        string? region = null;
        var interactive = false;

        for (var i = 0; i < rest.Length; i++)
        {
            var arg = rest[i];
            if (arg is "-s" or "--settings")
            {
                if (i + 1 < rest.Length)
                {
                    settings = rest[i + 1];
                    i++;
                }
            }
            else if (arg is "-r" or "--region")
            {
                if (i + 1 < rest.Length)
                {
                    region = rest[i + 1];
                    i++;
                }
            }
            else if (arg is "-I" or "--interactive")
            {
                interactive = true;
            }
        }

        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["settings"] = settings,
            ["region"] = region,
            ["interactive"] = interactive ? "true" : "false"
        };
        return new ParsedArgs(CommandKind.Migrate, dict);
    }

    private static ParsedArgs ParseAssess(ReadOnlySpan<string> rest)
    {
        string? input = null;
        string? output = null;
        string? profile = null;
        string? region = null;
        string? format = null;

        for (var i = 0; i < rest.Length; i++)
        {
            var arg = rest[i];
            if (arg is "-f" or "--format")
            {
                if (i + 1 < rest.Length) { format = rest[i + 1]; i++; }
            }
            else if (arg is "-o" or "--output")
            {
                if (i + 1 < rest.Length) { output = rest[i + 1]; i++; }
            }
            else if (arg is "-p" or "--profile")
            {
                if (i + 1 < rest.Length) { profile = rest[i + 1]; i++; }
            }
            else if (arg is "-r" or "--region")
            {
                if (i + 1 < rest.Length) { region = rest[i + 1]; i++; }
            }
            else if (!arg.StartsWith('-'))
            {
                input ??= arg;
            }
        }

        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = input,
            ["output"] = output,
            ["profile"] = profile,
            ["region"] = region,
            ["format"] = format
        };
        return new ParsedArgs(CommandKind.Assess, dict);
    }

    private static ParsedArgs ParseVerify(ReadOnlySpan<string> rest)
    {
        string? input = null;
        string? endpoint = null;
        string? region = null;
        string? settings = "migration-settings.json";
        string? dashboardId = null;
        var interactive = false;

        for (var i = 0; i < rest.Length; i++)
        {
            var arg = rest[i];
            if (arg is "-e" or "--endpoint")
            {
                if (i + 1 < rest.Length)
                {
                    endpoint = rest[i + 1];
                    i++;
                }
            }
            else if (arg is "-r" or "--region")
            {
                if (i + 1 < rest.Length)
                {
                    region = rest[i + 1];
                    i++;
                }
            }
            else if (arg is "-s" or "--settings")
            {
                if (i + 1 < rest.Length)
                {
                    settings = rest[i + 1];
                    i++;
                }
            }
            else if (arg is "-d" or "--dashboard-id")
            {
                if (i + 1 < rest.Length)
                {
                    dashboardId = rest[i + 1];
                    i++;
                }
            }
            else if (arg is "-I" or "--interactive")
            {
                interactive = true;
            }
            else if (!arg.StartsWith('-'))
            {
                input ??= arg;
            }
        }

        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = input,
            ["endpoint"] = endpoint,
            ["region"] = region,
            ["settings"] = settings,
            ["dashboard-id"] = dashboardId,
            ["interactive"] = interactive ? "true" : "false"
        };
        return new ParsedArgs(CommandKind.Verify, dict);
    }

    private static ParsedArgs ParseImport(ReadOnlySpan<string> rest)
    {
        string? input = null;
        string? endpoint = null;
        string? region = null;
        string? settings = "migration-settings.json";
        var interactive = false;

        for (var i = 0; i < rest.Length; i++)
        {
            var arg = rest[i];
            if (arg is "-e" or "--endpoint")
            {
                if (i + 1 < rest.Length)
                {
                    endpoint = rest[i + 1];
                    i++;
                }
            }
            else if (arg is "-r" or "--region")
            {
                if (i + 1 < rest.Length)
                {
                    region = rest[i + 1];
                    i++;
                }
            }
            else if (arg is "-s" or "--settings")
            {
                if (i + 1 < rest.Length)
                {
                    settings = rest[i + 1];
                    i++;
                }
            }
            else if (arg is "-I" or "--interactive")
            {
                interactive = true;
            }
            else if (!arg.StartsWith('-'))
            {
                input ??= arg;
            }
        }

        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = input,
            ["endpoint"] = endpoint,
            ["region"] = region,
            ["settings"] = settings,
            ["interactive"] = interactive ? "true" : "false"
        };
        return new ParsedArgs(CommandKind.Import, dict);
    }

    private static ParsedArgs ParseGrafanaImport(ReadOnlySpan<string> rest)
    {
        string? input = null;
        string? endpoint = null;
        string? region = null;
        string? settings = "migration-settings.json";
        string? overwrite = null;
        string? recursive = null;
        var interactive = false;
        var dryRun = false;

        for (var i = 0; i < rest.Length; i++)
        {
            var arg = rest[i];
            if (arg is "-e" or "--endpoint")
            {
                if (i + 1 < rest.Length)
                {
                    endpoint = rest[i + 1];
                    i++;
                }
            }
            else if (arg is "-r" or "--region")
            {
                if (i + 1 < rest.Length)
                {
                    region = rest[i + 1];
                    i++;
                }
            }
            else if (arg is "-s" or "--settings")
            {
                if (i + 1 < rest.Length)
                {
                    settings = rest[i + 1];
                    i++;
                }
            }
            else if (arg is "-I" or "--interactive")
            {
                interactive = true;
            }
            else if (arg is "-n" or "--dry-run")
            {
                dryRun = true;
            }
            else if (arg is "--overwrite")
            {
                overwrite = "true";
            }
            else if (arg is "--no-overwrite")
            {
                overwrite = "false";
            }
            else if (arg is "-R" or "--recursive")
            {
                recursive = "true";
            }
            else if (arg is "--no-recursive")
            {
                recursive = "false";
            }
            else if (!arg.StartsWith('-'))
            {
                input ??= arg;
            }
        }

        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = input,
            ["endpoint"] = endpoint,
            ["region"] = region,
            ["settings"] = settings,
            ["interactive"] = interactive ? "true" : "false",
            ["dry-run"] = dryRun ? "true" : "false",
            ["overwrite"] = overwrite,
            ["recursive"] = recursive
        };
        return new ParsedArgs(CommandKind.GrafanaImport, dict);
    }
}

public enum CommandKind
{
    Interactive,
    Convert,
    Migrate,
    Assess,
    Verify,
    Import,
    GrafanaImport
}

public sealed record ParsedArgs(CommandKind Command, IReadOnlyDictionary<string, string?> Options)
{
    public string? Get(string key) => Options.TryGetValue(key, out var v) ? v : null;
    public bool GetBool(string key) => string.Equals(Get(key), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Null when the flag was not supplied, so a settings-file value still applies.
    /// </summary>
    /// <remarks>
    /// <see cref="GetBool"/> collapses "absent" and "explicitly false" into the same answer, which for a
    /// paired --x/--no-x flag would make omitting it silently override the configured value.
    /// </remarks>
    public bool? GetBoolOrNull(string key) =>
        Get(key) is { } value && !string.IsNullOrWhiteSpace(value)
            ? string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            : null;
}
