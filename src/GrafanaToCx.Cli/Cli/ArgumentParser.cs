namespace GrafanaToCx.Cli.Cli;

/// <summary>
/// Parses command-style arguments for compatibility with existing CLI usage.
/// No System.CommandLine dependency.
/// </summary>
public static class ArgumentParser
{
    public static ParsedArgs Parse(string[] args)
    {
        if (args.Length == 0)
            return new ParsedArgs(CommandKind.Interactive, new Dictionary<string, string?>());

        var cmd = args[0].ToLowerInvariant();
        var rest = args.AsSpan(1);

        return cmd switch
        {
            "convert" => ParseConvert(rest),
            "migrate" => ParseMigrate(rest),
            "assess" => ParseAssess(rest),
            "verify" => ParseVerify(rest),
            "import" => ParseImport(rest),
            _ => new ParsedArgs(CommandKind.Interactive, new Dictionary<string, string?>())
        };
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
            else if (arg is "-I" or "--interactive")
            {
                interactive = true;
            }
        }

        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["settings"] = settings,
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
        string? endpoint = "https://api.coralogix.com/mgmt/openapi/latest";
        string? dashboardId = null;

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
            else if (arg is "-d" or "--dashboard-id")
            {
                if (i + 1 < rest.Length)
                {
                    dashboardId = rest[i + 1];
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
            ["endpoint"] = endpoint,
            ["dashboard-id"] = dashboardId
        };
        return new ParsedArgs(CommandKind.Verify, dict);
    }

    private static ParsedArgs ParseImport(ReadOnlySpan<string> rest)
    {
        string? input = null;
        string? endpoint = "https://api.coralogix.com/mgmt/openapi/latest";

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
            else if (!arg.StartsWith('-'))
            {
                input ??= arg;
            }
        }

        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = input,
            ["endpoint"] = endpoint
        };
        return new ParsedArgs(CommandKind.Import, dict);
    }
}

public enum CommandKind
{
    Interactive,
    Convert,
    Migrate,
    Assess,
    Verify,
    Import
}

public sealed record ParsedArgs(CommandKind Command, IReadOnlyDictionary<string, string?> Options)
{
    public string? Get(string key) => Options.TryGetValue(key, out var v) ? v : null;
    public bool GetBool(string key) => string.Equals(Get(key), "true", StringComparison.OrdinalIgnoreCase);
}
