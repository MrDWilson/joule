using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>A live start-up crashed with "failed to pin block (243.4 MiB/244.1 MiB used)" while checkpointing a 1.2 GB
/// database under the old fixed 256 MB cap. The cap now defaults to 1 GB and can be set with App__DatabaseMemoryLimit.</summary>
public sealed class DatabaseMemoryLimitTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "joule-memory-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(null, "1GB")]
    [InlineData("", "1GB")]
    [InlineData("2GB", "2GB")]
    [InlineData(" 512 MB ", "512 MB")]
    [InlineData("1.5GiB", "1.5GiB")]
    [InlineData("lots", "1GB")]
    [InlineData("1GB'; DROP TABLE x; --", "1GB")]
    public void OnlyWellFormedSizesAreUsed(string? configured, string expected) => Assert.Equal(expected, DataStore.ValidMemoryLimit(configured));

    [Fact]
    public void TheConfiguredLimitIsAppliedToTheDatabase()
    {
        using var db = new DataStore(path, memoryLimit: "768MB");
        Assert.Equal("768MB", db.MemoryLimit);
        var limit = Convert.ToString(db.Query("SELECT current_setting('memory_limit') AS limit")[0]["limit"]);
        Assert.Contains("MiB", limit);
        Assert.Equal("false", Convert.ToString(db.Query("SELECT current_setting('preserve_insertion_order') AS v")[0]["v"])!.ToLowerInvariant());
    }

    public void Dispose() { try { Directory.Delete(path, true); } catch (IOException) { } }
}
