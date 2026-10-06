using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class SocActualTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "joule-soc-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
    static TelemetrySample Soc(double minutes, double value) => new("soc", "sensor.soc", At.AddMinutes(minutes), value, "%", "HomeAssistant", value.ToString(System.Globalization.CultureInfo.InvariantCulture), "%", At.AddMinutes(minutes));
    public void Dispose() { try { Directory.Delete(path, true); } catch { } }

    [Fact]
    public void SlotSocActualUsesTheNearestObservedSampleBecausePollingNeverHitsTheBoundary()
    {
        using var db = new DataStore(path);
        db.SavePlan(new PlanSnapshot { Source = "Predbat", At = At.AddMinutes(-30), CollectedAt = At.AddMinutes(-30), Slots = [new(At, .5, null, 0, null, 40, null, 6.67, 15, "Chrg", .03), new(At.AddMinutes(30), .5, null, 0, null, 60, null, 6.67, 15, "Chrg", .03)] });
        // Samples at 29.6 and 31.2 minutes bracket the first slot end; 29.6 is nearer. The second slot end (60) has nothing within five minutes.
        db.SaveTelemetry([Soc(-0.4, 20), Soc(29.6, 41), Soc(31.2, 43), Soc(52, 55)]);
        var slots = db.GetPlan()!.Slots;
        Assert.Equal(41, slots[0].SocActual); Assert.Null(slots[1].SocActual);
        Assert.Equal(41, db.ReadRecentTimeline(At.AddHours(-1), At.AddHours(1))[0].SocActual);
    }
}
