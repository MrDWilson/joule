namespace Joule;

public record TelemetrySample(string Metric, string EntityId, DateTimeOffset Time, double? Value, string Unit, string Source, string RawState, string RawUnit, DateTimeOffset? SourceUpdatedAt = null, string AttributesJson = "{}", string Status = "observed");

/// <summary>A stretch of a window that a meter could not account for, and why: offline (Home Assistant said unavailable or could
/// not be read), idle (the sensor said unknown when it should have been reporting), not_found (the mapped entity is missing),
/// invalid (an unreadable state or unit), no_samples (nothing was collected), reset (an unexplained counter drop) or
/// source_changed (a different entity).</summary>
public record EnergyGap(DateTimeOffset From, DateTimeOffset To, string Reason, double? KnownKwh = null);

/// <summary>A stretch where grid energy is known but could not be priced, or is not known at all.</summary>
public record CostGap(string Metric, DateTimeOffset From, DateTimeOffset To, string Reason);

/// <summary>
/// Energy for one meter over a window. EnergyKwh is the best figure: measured intervals plus "spread" intervals (energy proved by the
/// counter, timing unknown) and short estimated tails, prorated at the window edges. CoverageFraction is the share of the window
/// (from the first record to the latest poll) whose energy is known. MeasuredSeconds counts only slot-precise readings; idle time
/// that the sensor profile says is a known zero (solar overnight, a charger between sessions) is inside it and also in IdleSeconds.
/// </summary>
public record EnergyMetricSummary(double? EnergyKwh, double ObservedSeconds, double CoverageFraction, int MissingIntervals)
{
    /// <summary>Seconds covered by slot-precise (observed) intervals, including expected idle.</summary>
    public double MeasuredSeconds { get; init; }
    /// <summary>Seconds where the sensor reported unknown and the profile says that means zero (counted as measured).</summary>
    public double IdleSeconds { get; init; }
    /// <summary>Seconds and energy whose timing is estimated: spread across an outage, or a short tail before a reset.</summary>
    public double EstimatedSeconds { get; init; }
    public double? EstimatedKwh { get; init; }
    /// <summary>Energy the counters prove flowed during gaps that are too long to spread (null when it cannot be known).</summary>
    public double? MissingKwh { get; init; }
    public List<EnergyGap> Gaps { get; init; } = [];
    /// <summary>For daily counters on a window that starts at local midnight: the meter's own total (its last reading carrying that day's reset).</summary>
    public double? CounterDayTotalKwh { get; init; }
    /// <summary>True when EnergyKwh is within 0.05 kWh of CounterDayTotalKwh.</summary>
    public bool? Reconciled { get; init; }
    /// <summary>daily_counter, session_counter, solar_daily or lifetime_counter.</summary>
    public string? Profile { get; init; }
    /// <summary>complete, idle_zero (complete, with idle counted as zero), estimated (complete, partly ≈), partial, missing or no_records.</summary>
    public string State { get; init; } = "missing";
    /// <summary>The part of the window the coverage denominator uses: records start, and the latest poll when the window runs to now.</summary>
    public DateTimeOffset? CoverageFrom { get; init; }
    public DateTimeOffset? CoverageTo { get; init; }
}

/// <summary>
/// Totals for a window. NetCostGbp = ImportCostGbp − ExportCreditGbp, each side with its own coverage; it is the headline figure.
/// ObservedNetCostGbp is the stricter matched-period figure (only periods both meters cover with whole measured intervals), kept
/// for experiments; it is never the headline.
/// </summary>
public record EnergySummary(DateTimeOffset From, DateTimeOffset To, Dictionary<string, EnergyMetricSummary> Metrics, double? ImportCostGbp, double? ExportCreditGbp, double? ObservedNetCostGbp, double CostCoverageFraction, double CostObservedSeconds, string[] Sources, string[] Limitations)
{
    public double? NetCostGbp { get; init; }
    /// <summary>Share of the import (export) meter's coverage window whose cost is known.</summary>
    public double ImportCostCoverage { get; init; }
    public double ExportCostCoverage { get; init; }
    /// <summary>True when part of that side's cost is estimated: spread or tail energy, or a Predbat plan rate used while the tariff sensor was unavailable.</summary>
    public bool ImportCostEstimated { get; init; }
    public bool ExportCostEstimated { get; init; }
    /// <summary>Grid energy (kWh) that is known but could not be priced, plus energy the counters prove is missing from gaps.</summary>
    public double UnpricedGridKwh { get; init; }
    /// <summary>True when some grid energy is missing and its amount cannot be known (no reading either side of a gap).</summary>
    public bool GridEnergyUnknown { get; init; }
    /// <summary>The part of import cost plus export credit (absolute £) that rests on estimates: spread energy or plan rates.</summary>
    public double EstimatedCostGbp { get; init; }
    public List<CostGap> CostGaps { get; init; } = [];
    /// <summary>Home use excluding the car: load − ev, when the load meter includes EV charging and the EV meter covers the window.</summary>
    public EnergyMetricSummary? Home { get; init; }
    /// <summary>Whether the mapped load meter includes EV charging (configured, or detected from history); null when unknown.</summary>
    public bool? LoadIncludesEv { get; init; }
    /// <summary>The standing charge for the window (£): each local day's rate times the share of that day covered, from Joule's first
    /// reading up to now. Null when no rate is known. It is never part of NetCostGbp.</summary>
    public double? StandingChargeGbp { get; init; }
    /// <summary>The standing charge rate on the window's last day, in pence per day.</summary>
    public double? StandingChargePencePerDay { get; init; }
    /// <summary>sensor (Home Assistant) or manual (typed into Setup).</summary>
    public string? StandingChargeSource { get; init; }
    /// <summary>True when an early day took the first rate Joule recorded, because none was recorded that day.</summary>
    public bool StandingChargeAssumed { get; init; }
    /// <summary>The owner's choice: whether the headline net cost includes the standing charge (default yes when it is known).</summary>
    public bool StandingChargeIncluded { get; init; }
    /// <summary>NetCostGbp plus StandingChargeGbp, when both are known: the whole bill for the window.</summary>
    public double? NetCostWithStandingChargeGbp { get; init; }
}

public record MatchedForecast(DateTimeOffset Time, double Forecast, double Actual, double DurationSeconds, string Method = "Exact slot measurement");
public record PlanReference(string Id, DateTimeOffset At, string Source, DateTimeOffset? CollectedAt = null);
public record PlanPage(List<PlanReference> Items, long Total, int Offset, int Limit);
public static class TelemetrySchema
{
    public static readonly string[] EnergyMetrics = ["load", "pv", "grid_import", "grid_export", "battery_charge", "battery_discharge", "ev"];
    public const string Description = "telemetry_samples stores explicitly mapped HA sensors with normalized value (kWh cumulative, % SoC, p/kWh tariffs), UTC sample time, raw_state/raw_unit, entity_id, source_updated_at, attributes_json, source and status. Sample time is Home Assistant's last_updated when the reading changed, otherwise the poll time (a heartbeat that confirms an unchanged reading). Successful sample and interval status is exactly 'observed', not 'valid' or 'ok'. Sample statuses: observed; idle (Home Assistant said unknown: normal for solar overnight, an EV charger between sessions, an export counter before the first export of the day); unavailable (device offline or Home Assistant unreachable); not_found (the mapped entity does not exist); unsupported_unit; invalid. observations stores Predbat entity scalar states with their exact entity_id (status normally predbat.status for the default prefix), value and unit at collection time; it has no status column or state attributes. Discover stored entity IDs instead of guessing a sensor domain. source is the transport: HomeAssistant (direct) or Predbat mirror (the same entity read via Predbat's HA state mirror); intervals break on entity change, not transport change. telemetry_intervals stores meter energy between readings: metric,start_time,end_time,energy_kwh,source,status,import_cost_gbp,export_credit_gbp,method,cost_method,missing_kwh. Interval status: observed (slot-precise energy; method counter, flat, idle = expected-idle zero, reset = Home Assistant total_increasing reset where the new value is the energy since the reset, reset_split = a daily counter split at its last_reset, reset_after_idle = the first reading after an idle run credited to the final poll, session_start; a restart is credited with its whole new reading only when that implies at most 25 kW, otherwise the drop is flat or an unknown reset); spread (energy proved by the counter across an outage of up to two hours or since a reset, timing unknown: counts in totals and cost, not in per-slot accuracy or power peaks; method spread or tail = estimated energy before a midnight reset or at the end of an EV session); gap, invalid, reset and source_changed have NULL energy, never zero (method gives the reason: offline, idle, not_found, invalid, no_samples; missing_kwh is the counter difference when known). cost_method is tariff (the tariff sensor, split at rate changes) or plan_rate (Predbat's plan rate while the tariff sensor was unavailable). actual_energy and predbat_actual_intervals are retained legacy diagnostic derivations from Predbat's load_energy_actual curve, NOT measured load: the native curve may contain forecast tails, EV/iBoost filtering and base-load adjustments. Their load_actual column names do not confer measurement provenance. Live plan_slots embedded actual fields are also unverified diagnostics; GetPlan supplies actuals from complete mapped meter coverage instead. Only labelled Demo embedded actuals are synthetic supported observations. Forecasts remain frozen. EnergySummary.netCostGbp is import cost minus export credit, each with its own coverage; observedNetCostGbp is the matched-period figure (both meters with whole measured intervals); both exclude the standing charge. standingChargeGbp is the daily standing charge prorated over the window (from the Octopus standing charge sensor or the owner's figure in Setup), netCostWithStandingChargeGbp adds it, and standingChargeIncluded says which of the two the owner wants as the headline. The standing_charges table holds one rate per local day (pence_per_day, source sensor or manual). A whole-house load meter usually includes EV charging; EnergySummary.home is load minus ev when that applies, which is what Predbat's load forecast predicts. Aggregate sensors do not prove physical battery-to-EV causality.";
    public static object Tables => new { telemetry_samples = "metric, entity_id, time, value, unit, source, raw_state, raw_unit, source_updated_at, attributes_json, status", telemetry_intervals = "metric, start_time, end_time, energy_kwh, source, status, import_cost_gbp, export_credit_gbp, method, cost_method, missing_kwh", standing_charges = "day (local date), pence_per_day, source (sensor or manual), entity_id, recorded_at; one standing charge rate per day", actual_energy = "recorded_at,time,load_actual; legacy unverified native-derived diagnostics only, NOT measured energy", predbat_actual_intervals = "start_time,end_time,recorded_at,load_actual,entity_id,source; legacy unverified native-derived diagnostics only, NOT measured energy", plan_slots = "snapshot_id,captured_at,time,duration_minutes,load_forecast,load_actual,pv_forecast,pv_actual,soc_forecast,soc_actual,import_rate,export_rate,action,cost; embedded live actuals are unverified diagnostics, not measured observations" };
}

/// <summary>
/// The latest stored reading for a metric. The value is kept even when it is old: AgeSeconds and Stale say how old it is.
/// LastObservedValue/LastObservedAt are the last usable reading when the latest is idle or offline. Expected is true when a
/// non-observed state is normal for the sensor's profile (a charger between sessions, solar overnight); Reason explains it in plain words.
/// </summary>
public record LatestTelemetry(double? Value,string Unit,DateTimeOffset Time,string Status,string EntityId,string Source,string? RawState=null,string? RawUnit=null,DateTimeOffset? SourceUpdatedAt=null)
{
    public double? LastObservedValue { get; init; }
    public DateTimeOffset? LastObservedAt { get; init; }
    public bool Expected { get; init; } = true;
    public string? Reason { get; init; }
    public double AgeSeconds { get; init; }
    public bool Stale { get; init; }
    public string? Profile { get; init; }
}
public record PlanSlotEvidence(DateTimeOffset Time,int DurationMinutes,EnergySummary Actual,double? EstimatedLoadKwh,double? EstimatedPvKwh,string EstimateMethod);
public record PlanEvidence(string PlanId,List<PlanSlotEvidence> Slots);
