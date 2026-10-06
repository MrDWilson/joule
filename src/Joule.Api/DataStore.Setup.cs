namespace Joule;

public partial class DataStore
{
    /// <summary>The newest stored copy of Predbat's /api/state (only Predbat's own entities are kept), or null when none
    /// has been collected yet. Used by Setup to suggest sensors; never sent to the browser as-is.</summary>
    public string? ReadLatestSourceState()
    {
        lock (gate)
        {
            using var cmd = Command("SELECT state_json, state_gz FROM source_snapshots ORDER BY recorded_at DESC LIMIT 1");
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? SnapshotText(reader, 0, 1) : null;
        }
    }
}
