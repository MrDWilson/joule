using DuckDB.NET.Data;

namespace Joule;

/// <summary>
/// Names of the files Joule keeps in its data directory. Joule was called Predbat AI before 1.0, and installs from then
/// still have files under the old names; they are renamed once, on the first start of a version that knows both.
/// </summary>
public static class DataFiles
{
    public const string Database = "joule.duckdb";
    public const string LegacyDatabase = "predbat.duckdb";
    /// <summary>DuckDB's write-ahead log sits next to the database as "&lt;database&gt;.wal".</summary>
    public const string WalSuffix = ".wal";
    /// <summary>Marks the demo configuration folder as Joule's own, so the demo never writes into a folder it didn't create.</summary>
    public const string DemoMarker = ".joule-demo";
    public const string LegacyDemoMarker = ".predbat-ai-demo";

    /// <summary>The database file to open, and a line for the log when something happened to get there.</summary>
    /// <param name="Warning">True when the old file couldn't be renamed and is used where it is.</param>
    public sealed record DatabaseChoice(string Path, string? Note, bool Warning);

    /// <summary>
    /// Picks the database in <paramref name="directory"/>: joule.duckdb when it exists; otherwise an older predbat.duckdb
    /// is renamed to joule.duckdb (its write-ahead log folded in first) and used. Nothing is ever deleted: if the rename
    /// can't happen (the file is in use, or the folder is read-only), the old file is used under its old name and the
    /// rename is tried again at the next start.
    /// </summary>
    /// <param name="checkpoint">Folds a database's write-ahead log into the file. Tests replace it to inject failures.</param>
    /// <param name="move">Renames a file (File.Move). Tests replace it to inject failures.</param>
    public static DatabaseChoice ChooseDatabase(string directory, Action<string>? checkpoint = null, Action<string, string>? move = null)
    {
        var current = System.IO.Path.Combine(directory, Database);
        var legacy = System.IO.Path.Combine(directory, LegacyDatabase);
        if (File.Exists(current))
            return new(current, File.Exists(legacy) ? $"Using {current}. An older {LegacyDatabase} in the same folder is left as it is; remove it once you no longer need it." : null, false);
        if (!File.Exists(legacy)) return new(current, null, false);
        var walMoved = false;
        try
        {
            // With the log folded in, the database is one file and one rename moves all of it.
            (checkpoint ?? Checkpoint)(legacy);
            // A log left under the new name without its database (an interrupted earlier attempt) would be replayed into
            // the renamed file. Its content is already in the file the checkpoint just wrote, so it is set aside, not used.
            SetAside(current + WalSuffix);
            // Normally gone after the checkpoint. If not, it moves first so the database never arrives without its log.
            if (File.Exists(legacy + WalSuffix)) { (move ?? File.Move)(legacy + WalSuffix, current + WalSuffix); walMoved = true; }
            (move ?? File.Move)(legacy, current);
            return new(current, $"Renamed {legacy} to {Database}, Joule's name for its database. This happens once.", false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DuckDBException)
        {
            // The old database stays in use, so a log that already moved goes back with it rather than being set aside next time.
            if (walMoved) try { File.Move(current + WalSuffix, legacy + WalSuffix); } catch (Exception back) when (back is IOException or UnauthorizedAccessException) { }
            return new(legacy, $"Couldn't rename {legacy} to {Database} ({e.Message}). Joule is using it under its old name and will try again at the next start.", true);
        }
    }

    /// <summary>Opens the database once, writes its log into the file and closes it, which leaves no .wal behind.</summary>
    static void Checkpoint(string path)
    {
        using var connection = new DuckDBConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CHECKPOINT";
        command.ExecuteNonQuery();
    }

    static void SetAside(string path)
    {
        if (File.Exists(path)) File.Move(path, $"{path}.set-aside-{DateTime.UtcNow:yyyyMMddHHmmss}");
    }
}
