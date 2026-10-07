using System.Text.RegularExpressions;

namespace Joule;

/// <summary>One local day's standing charge (pence per day, VAT included). Source: sensor (Home Assistant) or manual (Setup).</summary>
public sealed record StandingChargeDay(DateOnly Day, double PencePerDay, string Source, string? EntityId, DateTimeOffset RecordedAt);

/// <summary>The owner's standing charge choices: a manual figure (used when there is no sensor) and whether the headline net cost includes it.</summary>
public sealed record StandingChargePreferences(double? ManualPencePerDay, bool IncludeInNet = true);

/// <summary>POST /api/telemetry/standing-charge. A null field leaves that choice as it is; ClearManual removes the manual figure.</summary>
public sealed record StandingChargeRequest(double? ManualPencePerDay, bool? IncludeInNet, bool ClearManual = false);

/// <summary>
/// GET /api/telemetry/standing-charge: where the standing charge comes from and the figure for today. Entity is the Home Assistant
/// sensor Joule reads (EntityOrigin: "configured" when mapped, "octopus" when worked out from the Octopus Energy import rate sensor).
/// </summary>
public sealed record StandingChargeView(bool IncludeInNet, double? ManualPencePerDay, string? Entity, string? EntityOrigin, double? SensorPencePerDay, DateTimeOffset? SensorAt,
    string? SensorStatus, double? TodayPencePerDay, string? TodaySource, List<StandingChargeDay> Recent);

/// <summary>A window's share of the standing charge.</summary>
/// <param name="Gbp">The charge for the window: each local day's rate times the share of that day the window covers.</param>
/// <param name="PencePerDay">The rate on the window's last day.</param>
/// <param name="Assumed">True when a day had no recorded rate and took the nearest one recorded later (the first days before a rate was known).</param>
public sealed record StandingChargeAmount(double Gbp, double PencePerDay, string Source, bool Assumed);

/// <summary>
/// The standing charge: a fixed daily cost that the energy meters can't see. Joule reads it from the Octopus Energy integration's
/// "Current Standing Charge" sensor (sensor.octopus_energy_electricity_{serial}_{mpan}_current_standing_charge, in GBP per day), found
/// from the import rate sensor on the same meter, or from a sensor mapped in Setup; otherwise the owner types it in (pence per day).
/// </summary>
public static class StandingCharge
{
    public const string Metric = "standing_charge";
    static readonly Regex OctopusRate = new(@"^sensor\.octopus_energy_electricity_([a-z0-9]+)_([a-z0-9]+)_current_rate$", RegexOptions.CultureInvariant);

    /// <summary>The Octopus Energy standing charge sensor on the same meter as an Octopus import rate sensor, or null for any other sensor.
    /// Export meters (…_export_current_rate) have their own standing charge sensor, which isn't what a household pays.</summary>
    public static string? FromOctopusRate(string? importRateEntity) =>
        importRateEntity is not null && OctopusRate.Match(importRateEntity) is { Success: true } m
            ? $"sensor.octopus_energy_electricity_{m.Groups[1].Value}_{m.Groups[2].Value}_current_standing_charge"
            : null;

    /// <summary>A Home Assistant reading in pence per day: GBP or £ (per day) × 100, pence as is. Null for any other unit.</summary>
    public static double? Pence(double value, string unit)
    {
        var u = unit.Replace(" ", "").ToLowerInvariant();
        double? pence = u switch
        {
            "gbp" or "£" or "gbp/day" or "£/day" or "gbp/d" or "£/d" => value * 100,
            "p" or "p/day" or "p/d" or "pence" or "pence/day" or "gbx" or "gbx/day" => value,
            _ => null
        };
        return pence is { } p && double.IsFinite(p) && p >= 0 && p <= 1000 ? p : null;
    }

    /// <summary>
    /// The standing charge for [from, to): for each local day the window overlaps, that day's rate times the share of the day covered.
    /// Days are civil days in <paramref name="zone"/>, so a 23-hour or 25-hour day at a clock change still adds up to one day's charge.
    /// Null when no overlapped day has a rate.
    /// </summary>
    public static StandingChargeAmount? Prorate(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone, Func<DateOnly, (double Pence, string Source, bool Assumed)?> rate)
    {
        if (to <= from) return null;
        double total = 0; (double Pence, string Source, bool Assumed)? last = null; var assumed = false; var any = false;
        for (var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).Date); ; day = day.AddDays(1))
        {
            var start = CivilTime.FirstValidInstant(day.ToDateTime(TimeOnly.MinValue), zone);
            if (start >= to) break;
            var end = CivilTime.FirstValidInstant(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
            var left = start > from ? start : from; var right = end < to ? end : to;
            if (right <= left || end <= start) continue;
            if (rate(day) is not { } r) continue;
            total += r.Pence / 100 * ((right - left).TotalSeconds / (end - start).TotalSeconds);
            last = r; assumed |= r.Assumed; any = true;
        }
        return any && last is { } l ? new StandingChargeAmount(Math.Round(total, 6), l.Pence, l.Source, assumed) : null;
    }
}
