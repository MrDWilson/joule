namespace Joule;

public partial class DataStore
{
    /// <summary>When records began: the first usable energy-meter reading. Cached in memory and kept current by SaveTelemetry.</summary>
    internal DateTimeOffset? ReadFirstObservationAt()
    {
        lock (gate) return FirstObservationAt();
    }
}
