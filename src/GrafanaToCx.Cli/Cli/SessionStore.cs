using Newtonsoft.Json;

namespace GrafanaToCx.Cli.Cli;

/// <summary>
/// Reads and writes <see cref="InteractiveSession"/> files, one per session, so an operator can leave the
/// console and come back to the answers they already gave.
/// </summary>
/// <remarks>
/// <para>
/// Sessions live under the user's home directory rather than the working directory. A relative path would
/// resolve against the process CWD, which is exactly how <c>migration-settings.json</c> comes to be
/// invisible when the tool is run from the repository root — a session file that appears or disappears
/// depending on where you launched from is worse than no session file. Being outside the repository also
/// means it needs no .gitignore entry to stay untracked.
/// </para>
/// <para>
/// Nothing here throws for a bad store. A missing directory, an unreadable file and a malformed body all
/// degrade to "no session", because being unable to recall the last root directory must never be a reason
/// the console cannot start. That is the opposite of <c>CheckpointStore</c>, which lets a parse error
/// escape — there a corrupt file means the record of what has already been published is untrustworthy,
/// and continuing would republish or skip real dashboards.
/// </para>
/// </remarks>
public sealed class SessionStore
{
    /// <summary>
    /// Bounds the directory without needing a cleanup command. Old sessions are far likelier to be
    /// abandoned than resumed, and <c>--resume</c> lists them newest-first anyway.
    /// </summary>
    private const int MaxSessions = 20;

    private readonly string _rootDirectory;
    private readonly Action<string> _warn;

    /// <param name="rootDirectory">
    /// Overridden by tests so they never touch the real home directory. Production callers pass null.
    /// </param>
    /// <param name="warn">Overridden by tests to capture warnings. Defaults to stderr.</param>
    public SessionStore(string? rootDirectory = null, Action<string>? warn = null)
    {
        _rootDirectory = rootDirectory ?? DefaultRootDirectory();
        _warn = warn ?? Console.Error.WriteLine;
    }

    public static string DefaultRootDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".grafana-to-cx",
            "sessions");

    /// <summary>
    /// A new session, not yet on disk — it is written by the first <see cref="SaveAsync"/>, so merely
    /// starting the console and quitting leaves no file behind.
    /// </summary>
    public InteractiveSession Create()
    {
        var now = DateTimeOffset.UtcNow;
        return new InteractiveSession
        {
            Id = NewId(),
            CreatedAt = now,
            LastUsedAt = now
        };
    }

    /// <summary>
    /// The session whose id equals <paramref name="idOrPrefix"/>, or the single one it prefixes.
    /// </summary>
    /// <remarks>
    /// Prefix matching mirrors git's short hashes: the id is something the operator retypes from an exit
    /// message, and a few characters is enough. An exact match always wins outright, so an id that happens
    /// to prefix another is still addressable.
    /// </remarks>
    /// <returns>
    /// Null when nothing matched. Ambiguity is reported through <paramref name="ambiguous"/> instead of a
    /// null return, so the caller can name the candidates rather than claim the session does not exist.
    /// </returns>
    public InteractiveSession? Resolve(string idOrPrefix, out IReadOnlyList<string> ambiguous)
    {
        ambiguous = [];

        if (string.IsNullOrWhiteSpace(idOrPrefix)) return null;

        var trimmed = idOrPrefix.Trim();
        var all = List();

        var exact = all.FirstOrDefault(s => string.Equals(s.Id, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var matches = all
            .Where(s => s.Id.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1) return matches[0];

        if (matches.Count > 1)
            ambiguous = matches.Select(s => s.Id).ToList();

        return null;
    }

    public InteractiveSession? MostRecent() => List().FirstOrDefault();

    /// <summary>Every readable session, most recently used first. Unreadable files are warned about and skipped.</summary>
    public IReadOnlyList<InteractiveSession> List()
    {
        if (!Directory.Exists(_rootDirectory)) return [];

        string[] files;
        try
        {
            files = Directory.GetFiles(_rootDirectory, "*.json");
        }
        catch (IOException ex)
        {
            _warn($"Warning: could not read the session directory '{_rootDirectory}' ({ex.Message}).");
            return [];
        }
        catch (UnauthorizedAccessException ex)
        {
            _warn($"Warning: could not read the session directory '{_rootDirectory}' ({ex.Message}).");
            return [];
        }

        return files
            .Select(TryRead)
            .OfType<InteractiveSession>()
            .OrderByDescending(s => s.LastUsedAt)
            .ToList();
    }

    /// <summary>Writes <paramref name="session"/>, stamping <see cref="InteractiveSession.LastUsedAt"/>.</summary>
    /// <remarks>
    /// Called after every completed menu action rather than only at exit: the console returns to the menu
    /// after a failed action, and a Ctrl-C or a crash should not discard answers already given.
    /// </remarks>
    public async Task SaveAsync(InteractiveSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        session.LastUsedAt = DateTimeOffset.UtcNow;

        try
        {
            Directory.CreateDirectory(_rootDirectory);
            var json = JsonConvert.SerializeObject(session, Formatting.Indented);
            await File.WriteAllTextAsync(PathFor(session.Id), json, ct);
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Losing the remembered answers is a lesser harm than failing the action that just succeeded.
            _warn($"Warning: could not save session '{session.Id}' ({ex.Message}). Answers will not be remembered.");
        }
    }

    private void Prune()
    {
        var stale = List().Skip(MaxSessions).ToList();

        foreach (var session in stale)
        {
            try
            {
                File.Delete(PathFor(session.Id));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that will not delete is harmless; it is only taking up space.
                _warn($"Warning: could not remove old session '{session.Id}' ({ex.Message}).");
            }
        }
    }

    private InteractiveSession? TryRead(string file)
    {
        try
        {
            var session = JsonConvert.DeserializeObject<InteractiveSession>(File.ReadAllText(file));

            // An id-less body cannot be resumed or overwritten in place, so it is as good as absent.
            // Trusting the file stem instead would let a renamed file resolve under a name its own
            // contents disagree with.
            if (session is null || string.IsNullOrWhiteSpace(session.Id))
            {
                _warn($"Warning: ignoring session file '{file}' — it has no session id.");
                return null;
            }

            return session;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _warn($"Warning: ignoring unreadable session file '{file}' ({ex.Message}).");
            return null;
        }
    }

    private string PathFor(string id) => Path.Combine(_rootDirectory, $"{id}.json");

    /// <summary>
    /// Eight hex characters of a fresh guid — short enough to retype from the exit message, and with
    /// <see cref="MaxSessions"/> live sessions the odds of a collision are not worth code to handle.
    /// </summary>
    private static string NewId() => Guid.NewGuid().ToString("N")[..8];
}
