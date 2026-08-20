using Newtonsoft.Json;

namespace GrafanaToCx.Core.Migration;

public sealed class CheckpointStore
{
    private readonly string _filePath;
    private Dictionary<string, CheckpointEntry> _entries = new();

    public CheckpointStore(string filePath)
    {
        _filePath = filePath;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
            return;

        var json = await File.ReadAllTextAsync(_filePath, ct);
        _entries = JsonConvert.DeserializeObject<Dictionary<string, CheckpointEntry>>(json)
                   ?? new Dictionary<string, CheckpointEntry>();
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        var json = JsonConvert.SerializeObject(_entries, Formatting.Indented);
        await File.WriteAllTextAsync(_filePath, json, ct);
    }

    public CheckpointEntry? Get(string key) =>
        _entries.TryGetValue(key, out var entry) ? entry : null;

    /// <summary>
    /// Stores an entry under its Grafana UID. Used by the migrate flow, where the UID is unique per run.
    /// </summary>
    public void Upsert(CheckpointEntry entry) =>
        Upsert(entry.GrafanaUid, entry);

    /// <summary>
    /// Stores an entry under an explicit key. Used by the import flow, whose key also carries the target
    /// folder so the same source file imported into two folders gets two independent entries.
    /// </summary>
    public void Upsert(string key, CheckpointEntry entry) =>
        _entries[key] = entry;

    public IReadOnlyCollection<CheckpointEntry> All => _entries.Values;
}
