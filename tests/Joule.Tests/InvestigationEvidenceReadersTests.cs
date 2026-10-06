using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class InvestigationEvidenceReadersTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "joule-readers-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
    static TelemetrySample Sample(string metric, double minutes, double value) =>
        new(metric, "sensor." + metric, At.AddMinutes(minutes), value, metric == "soc" ? "%" : metric.EndsWith("tariff") ? "p/kWh" : "kWh", "HomeAssistant", value.ToString(System.Globalization.CultureInfo.InvariantCulture), "kWh", At.AddMinutes(minutes));
    public void Dispose() { try { Directory.Delete(path, true); } catch { } }

    [Fact]
    public void PlanVsActualJoinsTheFrozenPlanWithBoundaryAllocatedMeasurementsAndSocAtSlotEdges()
    {
        using var db = new DataStore(path);
        db.SavePlan(new PlanSnapshot { Source = "Predbat", At = At.AddMinutes(-30), CollectedAt = At.AddMinutes(-30), Slots = [new(At, .5, null, 0, null, 40, null, 6.67, 15, "Chrg", .03), new(At.AddMinutes(30), .4, null, 0, null, 60, null, 6.67, 15, "Demand", .02)] });
        // A later capture made after the first slot began is not a forecast of it, but it does count toward plan churn for the second slot.
        db.SavePlan(new PlanSnapshot { Source = "Predbat", At = At.AddMinutes(5), CollectedAt = At.AddMinutes(5), Slots = [new(At, .9, null, 0, null, 10, null, 6.67, 15, "Exp", .03), new(At.AddMinutes(30), .4, null, 0, null, 60, null, 6.67, 15, "Chrg", .02)] });
        var samples = new List<TelemetrySample>();
        // Jittered polling: never exactly on a slot boundary, so exact-coverage logic would yield nothing.
        for (var m = -4.9; m <= 65; m += 5) { samples.Add(Sample("load", m, 10 + m * .02)); samples.Add(Sample("battery_charge", m, 3 + m * .04)); }
        samples.Add(Sample("soc", -0.5, 20)); samples.Add(Sample("soc", 29.6, 40)); samples.Add(Sample("soc", 60.2, 61));
        db.SaveTelemetry(samples);
        var slots = db.ReadPlanVsActual(At, At.AddMinutes(60));
        Assert.Equal(2, slots.Count);
        var first = slots[0];
        // Stored actions are normalised to the glossary key (raw "Chrg" is kept in raw_action).
        Assert.Equal(At, first.Time); Assert.Equal("charge", first.PlannedAction); Assert.Equal(40, first.SocPlannedPercent); Assert.Equal(6.67, first.ImportRatePence);
        Assert.Equal(20, first.SocActualStartPercent); Assert.Equal(40, first.SocActualEndPercent);
        Assert.Equal(.6, first.LoadKwh!.Value, 2); Assert.Equal(1.2, first.BatteryChargeKwh!.Value, 2); Assert.Null(first.PvKwh);
        Assert.Equal(At.AddMinutes(-30), first.PlanCapturedAt); Assert.Equal(1, first.PlanSnapshots);
        var second = slots[1];
        Assert.Equal("charge", second.PlannedAction); Assert.Equal(2, second.PlanSnapshots); Assert.Equal(2, second.DistinctPlannedActions); Assert.Equal(61, second.SocActualEndPercent);
    }

    [Fact]
    public void PlanVsActualRejectsUnorderedOrOversizedWindows()
    {
        using var db = new DataStore(path);
        Assert.Throws<DomainException>(() => db.ReadPlanVsActual(At, At));
        Assert.Throws<DomainException>(() => db.ReadPlanVsActual(At, At.AddDays(4)));
    }

    [Fact]
    public void HourlyMeasuredAggregatesObservedIntervalsAndCarriesBatteryLevelAndTariffAtHourEnd()
    {
        using var db = new DataStore(path);
        var samples = new List<TelemetrySample>();
        for (var m = 0; m <= 120; m += 5) samples.Add(Sample("load", m, 10 + m * .02));
        samples.Add(Sample("soc", 55, 50)); samples.Add(Sample("soc", 115, 70));
        samples.Add(Sample("import_tariff", 10, 6.67)); samples.Add(Sample("import_tariff", 70, 31.73));
        db.SaveTelemetry(samples);
        var hours = db.ReadHourlyMeasured(At, At.AddHours(2));
        Assert.Equal(2, hours.Count);
        Assert.Equal(At, hours[0].Hour); Assert.Equal(1.2, hours[0].EnergyKwh["load"]!.Value, 6); Assert.Equal(1, hours[0].CoverageFraction, 6);
        Assert.Equal(50, hours[0].SocEndPercent); Assert.Equal(6.67, hours[0].ImportRatePence); Assert.Null(hours[0].EnergyKwh["pv"]);
        Assert.Equal(1.2, hours[1].EnergyKwh["load"]!.Value, 6); Assert.Equal(70, hours[1].SocEndPercent); Assert.Equal(31.73, hours[1].ImportRatePence);
    }

    [Fact]
    public void ChangesSinceCountsNewPlansAndMeasurements()
    {
        using var db = new DataStore(path);
        db.SavePlan(new PlanSnapshot { Source = "Predbat", At = At, CollectedAt = At, Slots = [new(At, .5, null, 0, null, 40, null, 6.67, 15, "Demand", .03)] });
        db.SaveTelemetry([Sample("load", 0, 10), Sample("load", 5, 10.1)]);
        var text = System.Text.Json.JsonSerializer.Serialize(db.ReadChangesSince(At.AddHours(-1)));
        Assert.Contains("\"newPlanSnapshots\":1", text); Assert.Contains("\"newTelemetrySamples\":2", text); Assert.Contains("\"newObservedIntervals\":1", text);
        Assert.Contains("\"newPlanSnapshots\":0", System.Text.Json.JsonSerializer.Serialize(db.ReadChangesSince(At.AddHours(1))));
    }
}
