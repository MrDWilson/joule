using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class SqlEvidenceTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "predbat-sql-evidence-" + Guid.NewGuid().ToString("N"));
    [Fact] public void MetricValueLoadIsAStringRatherThanAnExecutableLoadCommand()
    {
        using var db = new DataStore(path); var at = DateTimeOffset.UtcNow.AddMinutes(-5);
        db.SaveTelemetry([new("load", "sensor.load", at, 10, "kWh", "Fixture", "10", "kWh")]);
        Assert.Equal(1, Convert.ToInt32(db.Query("SELECT count(*) AS n FROM telemetry_samples WHERE metric='load'").Single()["n"]));
    }
    [Fact] public void StandardDoubledQuotesPreserveLiteralTextWithoutBanningWordsInsideIt()
    {
        using var db = new DataStore(path);
        var row = db.Query("SELECT 'it''s load' AS text").Single();
        Assert.Equal("it's load", row["text"]);
    }
    [Theory]
    [InlineData("SELECT * FROM source_snapshots WHERE 'safe'='safe'")]
    [InlineData("SELECT * FROM \"source_snapshots\"")]
    [InlineData("SELECT * FROM \"read_csv\"('/tmp/secret.csv')")]
    [InlineData("SELECT * FROM read_text('/etc/hosts')")]
    [InlineData("SELECT 'safe' AS text FROM \"investigation_evidence\"")]
    [InlineData("SELECT 'it''s safe' AS text FROM \"source_snapshots\"")]
    [InlineData("SELECT * FROM \"source_'safe'_snapshots\" JOIN source_snapshots ON true")]
    [InlineData("SELECT * FROM \"safe'source_snapshots'safe\"")]
    [InlineData("SELECT * FROM \"safe'read_csv'safe\"('/tmp/secret.csv')")]
    [InlineData("SELECT 'safe' AS text\nFROM query_table('source_snapshots')")]
    [InlineData("SELECT * FROM 'source_snapshots'")]
    [InlineData("SELECT * FROM 'investigation_evidence'")]
    [InlineData("SELECT * FROM 'application_state'")]
    [InlineData("SELECT * FROM plans JOIN 'source_snapshots' ON true")]
    [InlineData("SELECT * FROM plans, 'source_snapshots'")]
    [InlineData("SELECT * FROM ('source_snapshots')")]
    [InlineData("WITH secrets AS (SELECT * FROM 'source_snapshots') SELECT * FROM secrets")]
    [InlineData("SELECT 'source_snapshots' AS text")]
    [InlineData("SELECT 'read_csv' AS text")]
    public void LiteralMaskingCannotHideForbiddenRelationsOrExternalFunctions(string sql)
    {
        using var db = new DataStore(path);
        Assert.Throws<DomainException>(() => db.Query(sql));
    }
    [Theory]
    [InlineData("SELECT E'ordinary escaped string' AS text")]
    [InlineData("SELECT E'backslash \\' read_csv' AS text")]
    [InlineData("SELECT 'backslash \\' read_csv' AS text")]
    [InlineData("SELECT $$ordinary dollar string$$ AS text")]
    [InlineData("SELECT $tag$ordinary dollar string$tag$ AS text")]
    [InlineData("SELECT U&'ordinary unicode string' AS text")]
    [InlineData("SELECT 'unterminated string AS text")]
    public void UnsupportedOrUnterminatedLiteralFormsAreRejectedBeforeExecution(string sql)
    {
        using var db = new DataStore(path);
        Assert.Throws<DomainException>(() => db.Query(sql));
    }
    [Fact] public void DemoSeedCanRepeatWithoutReseedingRetainedHistory()
    {
        using var db = new DataStore(path); var now = DateTimeOffset.UtcNow;
        var end = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute / 5 * 5, 0, TimeSpan.Zero);
        var previous = end.AddMinutes(-5);
        db.SaveTelemetry([new("load", "demo.load", previous, 10, "kWh", "Demo telemetry (scripted)", "10", "kWh")]);
        DemoTelemetry.Seed(db); DemoTelemetry.Seed(db);
        var samples = db.ReadTelemetrySamples(previous.AddMinutes(-1), end.AddMicroseconds(1), "load", 0, 200);
        Assert.Equal(2, samples.Count); Assert.Equal(end, db.ReadLatestTelemetry()["load"].Time);
        Assert.Equal(2, Convert.ToInt32(db.Query("SELECT count(*) AS n FROM telemetry_samples WHERE metric='load'").Single()["n"]));
    }
    public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, true); }
}
