using DuckDB.NET.Data;
using Joule;
using Microsoft.Extensions.Logging;
using Xunit;
namespace Joule.Tests;

/// <summary>Installs from before the rename keep their data: predbat.duckdb becomes joule.duckdb once, and nothing is lost.</summary>
public class DataFileRenameTests : IDisposable
{
    readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "joule-rename-" + Guid.NewGuid().ToString("N"));
    string Current => Path.Combine(directory, DataFiles.Database);
    string Legacy => Path.Combine(directory, DataFiles.LegacyDatabase);
    public DataFileRenameTests() => Directory.CreateDirectory(directory);
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact]
    public void AFreshDirectoryGetsTheNewNameAndNothingIsLogged()
    {
        var log = new ListLogger();
        using var db = new DataStore(directory, logger: log);
        Assert.Equal(Current, db.DatabasePath);
        Assert.True(File.Exists(Current));
        Assert.False(File.Exists(Legacy));
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void AnOldDatabaseIsRenamedOnceWithItsDataAndOneLogLine()
    {
        var state = DemoData.Create();
        state.AnalysisError = "kept across the rename";
        using (var original = new DataStore(directory)) original.Save(state);
        File.Move(Current, Legacy);

        var log = new ListLogger();
        using (var db = new DataStore(directory, logger: log))
        {
            Assert.Equal(Current, db.DatabasePath);
            Assert.Equal("kept across the rename", db.Load()!.AnalysisError);
            Assert.True(db.ReadStorageReport("dry-run", 30).DbSizeBytes > 0);
        }
        Assert.False(File.Exists(Legacy));
        Assert.False(File.Exists(Legacy + DataFiles.WalSuffix));
        var line = Assert.Single(log.Lines);
        Assert.Contains("Renamed", line.Message);
        Assert.Equal(LogLevel.Information, line.Level);

        var again = new ListLogger();
        using (var db = new DataStore(directory, logger: again)) Assert.Equal("kept across the rename", db.Load()!.AnalysisError);
        Assert.Empty(again.Lines);
    }

    [Fact]
    public void WritesStillOnlyInTheOldWriteAheadLogSurviveTheRename()
    {
        using (var old = new DuckDBConnection($"Data Source={Legacy}"))
        {
            old.Open();
            Run(old, "PRAGMA disable_checkpoint_on_shutdown");
            Run(old, "CHECKPOINT");
            Run(old, "CREATE TABLE fixture(v INTEGER)");
            Run(old, "INSERT INTO fixture VALUES (42)");
        }
        Assert.True(File.Exists(Legacy + DataFiles.WalSuffix), "The fixture needs rows that exist only in the write-ahead log.");

        using (new DataStore(directory)) { }

        Assert.False(File.Exists(Legacy));
        Assert.False(File.Exists(Legacy + DataFiles.WalSuffix));
        using var renamed = new DuckDBConnection($"Data Source={Current}");
        renamed.Open();
        using var command = renamed.CreateCommand();
        command.CommandText = "SELECT v FROM fixture";
        Assert.Equal(42, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public void WhenTheRenameFailsTheOldFileIsUsedWhereItIsAndLeftAlone()
    {
        File.WriteAllText(Legacy, "stand-in for the old database");
        File.WriteAllText(Legacy + DataFiles.WalSuffix, "stand-in for its log");

        var choice = DataFiles.ChooseDatabase(directory, _ => throw new IOException("in use by another process"));

        Assert.Equal(Legacy, choice.Path);
        Assert.True(choice.Warning);
        Assert.Contains("in use by another process", choice.Note);
        Assert.Equal("stand-in for the old database", File.ReadAllText(Legacy));
        Assert.Equal("stand-in for its log", File.ReadAllText(Legacy + DataFiles.WalSuffix));
        Assert.False(File.Exists(Current));
        Assert.False(File.Exists(Current + DataFiles.WalSuffix));
    }

    [Fact]
    public void AReadOnlyDataFolderKeepsTheOldName()
    {
        // Folder permissions are Unix-only here, and root ignores them.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        File.WriteAllText(Legacy, "old database");
        // A folder Joule can read but not change: the old file stays where it is, under its old name.
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var choice = DataFiles.ChooseDatabase(directory, _ => { });
            Assert.Equal(Legacy, choice.Path);
            Assert.True(choice.Warning);
            Assert.Equal("old database", File.ReadAllText(Legacy));
            Assert.False(File.Exists(Current));
        }
        finally { File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void WhenBothNamesExistTheNewOneWinsAndTheOldOneIsKept()
    {
        File.WriteAllText(Current, "new");
        File.WriteAllText(Legacy, "old");

        var choice = DataFiles.ChooseDatabase(directory, _ => throw new InvalidOperationException("must not touch the old file"));

        Assert.Equal(Current, choice.Path);
        Assert.False(choice.Warning);
        Assert.Contains(DataFiles.LegacyDatabase, choice.Note);
        Assert.Equal("old", File.ReadAllText(Legacy));
        Assert.Equal("new", File.ReadAllText(Current));
    }

    [Fact]
    public void AnOrphanedLogUnderTheNewNameIsSetAsideNotReplayedOrDeleted()
    {
        File.WriteAllText(Legacy, "old database");
        File.WriteAllText(Current + DataFiles.WalSuffix, "left by an interrupted rename");

        var choice = DataFiles.ChooseDatabase(directory, _ => { });

        Assert.Equal(Current, choice.Path);
        Assert.Equal("old database", File.ReadAllText(Current));
        Assert.False(File.Exists(Current + DataFiles.WalSuffix));
        var setAside = Assert.Single(Directory.GetFiles(directory, DataFiles.Database + DataFiles.WalSuffix + ".set-aside-*"));
        Assert.Equal("left by an interrupted rename", File.ReadAllText(setAside));
    }

    [Fact]
    public void ALogTheCheckpointLeftBehindMovesWithItsDatabase()
    {
        File.WriteAllText(Legacy, "old database");
        File.WriteAllText(Legacy + DataFiles.WalSuffix, "old log");

        var choice = DataFiles.ChooseDatabase(directory, _ => { });

        Assert.Equal(Current, choice.Path);
        Assert.Equal("old database", File.ReadAllText(Current));
        Assert.Equal("old log", File.ReadAllText(Current + DataFiles.WalSuffix));
        Assert.False(File.Exists(Legacy));
        Assert.False(File.Exists(Legacy + DataFiles.WalSuffix));
    }

    [Fact]
    public void TheDemoFolderMarkerTakesTheNewName()
    {
        var root = Path.Combine(directory, "demo-config");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, DataFiles.LegacyDemoMarker), "Joule owned demo configuration directory\n");
        File.WriteAllText(Path.Combine(root, "runtime-settings.json"), "{}");

        var files = new ConfigFileArchive(new() { Root = root, ArchiveDirectory = Path.Combine(directory, "archive"), AllowedFiles = ["runtime-settings.json"], DemoRuntimeMirror = true });

        Assert.True(files.Enabled);
        Assert.True(File.Exists(Path.Combine(root, DataFiles.DemoMarker)));
        Assert.False(File.Exists(Path.Combine(root, DataFiles.LegacyDemoMarker)));
    }

    [Fact]
    public void ANewDemoFolderGetsTheNewMarker()
    {
        var root = Path.Combine(directory, "demo-config");
        _ = new ConfigFileArchive(new() { Root = root, ArchiveDirectory = Path.Combine(directory, "archive"), AllowedFiles = ["runtime-settings.json"], DemoRuntimeMirror = true });
        Assert.True(File.Exists(Path.Combine(root, DataFiles.DemoMarker)));
        Assert.False(File.Exists(Path.Combine(root, DataFiles.LegacyDemoMarker)));
        // Starting again finds its own folder.
        _ = new ConfigFileArchive(new() { Root = root, ArchiveDirectory = Path.Combine(directory, "archive"), AllowedFiles = ["runtime-settings.json"], DemoRuntimeMirror = true });
    }

    static void Run(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    sealed record Line(LogLevel Level, string Message);
    sealed class ListLogger : ILogger
    {
        public List<Line> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(new(level, formatter(state, exception)));
    }
}
