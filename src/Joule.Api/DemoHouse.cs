using System.Collections.Concurrent;

namespace Joule;

/// <summary>
/// The sample household's energy, half-hour by half-hour, shared by the demo plan (<see cref="DemoData.Plan"/>) and the demo meters
/// (<see cref="DemoTelemetry"/>) so the battery, charging, export, import and prices agree everywhere.
///
/// Each local day runs Predbat's state schedule (overnight charge to 94%, freeze and hold in the morning, solar into the battery,
/// freeze export over the peak, an evening export down to 60%, a car hold on car evenings, the battery running the evening) through a
/// 13.5 kWh battery. The plan simulates that schedule with the forecast load and solar; the meters simulate it with what the house
/// "really" used and generated. Grid import and export are what is left over in each half-hour, so the meters always balance.
/// Slot i of a day starts i half-hours after local midnight (elapsed time), which keeps both sides aligned across clock changes.
/// </summary>
public static class DemoHouse
{
    public const double CapacityKwh = 13.5, ReservePercent = 4, ChargeTarget = 94, ChargeEnd = 93, MorningTarget = 90, ExportTarget = 60;
    /// <summary>Overnight session 00:30–02:00 at 5 kW every night (inside the 23:30–05:30 cheap window).</summary>
    public const int NightCarFrom = 1, NightCarTo = 3;
    public const double NightCarKwh = 2.5;
    /// <summary>Evening top-up 19:00–20:30 at 3.2 kW on car evenings, while the plan holds the battery for the car.</summary>
    public const int EveningCarFrom = 38, EveningCarTo = 40;
    public const double EveningCarKwh = 1.6;
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    public static TimeZoneInfo Zone => London;

    /// <summary>Import price (p/kWh) at a local clock hour: 7p in the 23:30–05:30 cheap window, 25.4p otherwise.</summary>
    public static double ImportRate(double localHour) => localHour < 5.5 || localHour >= 23.5 ? 7 : 25.4;
    /// <summary>Export price (p/kWh): 18p in the 16:00–19:00 peak, 15p otherwise.</summary>
    public static double ExportRate(double localHour) => localHour >= 16 && localHour < 19 ? 18 : 15;
    public static double ImportRate(DateTimeOffset local) => ImportRate(local.Hour + local.Minute / 60d);
    /// <summary>The demo house's standing charge, pence per day (a typical UK electricity rate).</summary>
    public const double StandingChargePence = 53.68;
    public static double ExportRate(DateTimeOffset local) => ExportRate(local.Hour + local.Minute / 60d);

    /// <summary>The car charges in the evening every other day; the plan holds the battery for it only on those evenings.</summary>
    public static bool CarEvening(DateOnly date) => date.DayNumber % 2 == 1;
    /// <summary>The first instant of a local date.</summary>
    public static DateTimeOffset Midnight(DateOnly date, TimeZoneInfo? zone = null) => CivilTime.FirstValidInstant(date.ToDateTime(TimeOnly.MinValue), zone ?? London);
    public static DateOnly LocalDate(DateTimeOffset at, TimeZoneInfo? zone = null) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone ?? London).Date);
    /// <summary>The half-hour slot (0–47) containing <paramref name="at"/> on its local date, by elapsed time since local midnight.</summary>
    public static int SlotAt(DateTimeOffset at, TimeZoneInfo? zone = null) => (int)Math.Clamp(Math.Floor((at - Midnight(LocalDate(at, zone), zone)).TotalMinutes / 30), 0, 47);

    /// <summary>Peak solar power (kW) for the season: 4.5·(0.35 + 0.65·sin²(π·day-of-year/365)), about 2.9 kW in early October.</summary>
    public static double PvPeakKw(DateOnly date) { var s = Math.Sin(Math.PI * date.DayOfYear / 365); return 4.5 * (.35 + .65 * s * s); }
    /// <summary>Clear-sky generation (kWh) in a half-hour slot: a sin³ arc across the day's daylight (about 07:15–18:20 in early
    /// October), centred on local solar noon. A clear October day makes about 14 kWh.</summary>
    public static double ClearPvKwh(DateOnly date, int slot)
    {
        var length = 12.2 + 4.4 * Math.Sin(2 * Math.PI * (date.DayOfYear - 80) / 365);
        var noon = London.IsDaylightSavingTime(date.ToDateTime(new TimeOnly(12, 0))) ? 12.8 : 11.8;
        var x = (slot * .5 + .25 - (noon - length / 2)) / length;
        return x is <= 0 or >= 1 ? 0 : .5 * PvPeakKw(date) * Math.Pow(Math.Sin(Math.PI * x), 3);
    }
    /// <summary>The day's cloudiness: 0.65 (overcast) to 1 (clear), so early-October days make about 8–14 kWh of solar.</summary>
    public static double Cloud(DateOnly date) => .65 + .35 * Noise(date.DayNumber, 0, 1);

    // Household load excluding the car (kWh per half-hour): an overnight base, a breakfast shoulder, lunch, and an evening that
    // tails off after 21:00. Cooking, kettles and noise are added per day on top of this.
    static readonly double[] BaseLoad =
    [
        .17, .16, .16, .15, .15, .15, .15, .16, .16, .17, .17,  // 00:00-05:30
        .18, .22, .32, .40, .38, .32, .27,                     // 05:30-09:00 breakfast
        .24, .23, .22, .22, .24, .28, .36, .32,                // 09:00-13:00 lunch
        .24, .22, .21, .21, .23, .26, .30, .32,                // 13:00-17:00
        .34, .34, .34, .34, .36, .36, .34, .33,                // 17:00-21:00
        .30, .28, .25, .22, .20, .18,                          // 21:00-24:00
    ];
    static readonly double[] Oven = [.45, .55, .35];
    /// <summary>The evening cooking peak moves day to day (17:00, 18:00 or 17:30), so yesterday and last week never match today.</summary>
    public static int OvenSlot(DateOnly date) => 34 + ((date.DayNumber % 3) switch { 0 => 0, 1 => 2, _ => 1 });

    /// <summary>Measured household load (kWh, excluding the car) in a slot: a stepped profile with noise, a kettle at breakfast and
    /// teatime, and the oven at the day's cooking time. Evenings after 21:00 run lighter than the forecast expects.</summary>
    public static double LoadKwh(DateOnly date, int slot)
    {
        var d = date.DayNumber;
        var load = BaseLoad[slot] * (.9 + .2 * Noise(d, slot, 3));
        var oven = slot - OvenSlot(date);
        if (oven is >= 0 and < 3) load += Oven[oven] * (.85 + .3 * Noise(d, 0, 4));
        if (slot == 13 + (int)(3 * Noise(d, 0, 5))) load += .15;
        if (slot == 31 + (int)(2 * Noise(d, 0, 6))) load += .12;
        if (slot >= 42) load *= .92;
        return Math.Round(load, 3);
    }
    /// <summary>Forecast household load: the typical profile with the cooking peak spread over its usual window, scaled by
    /// load_scaling 1.08 (and a little more after 21:00, the overestimate the demo investigation finds).</summary>
    public static double ForecastLoadKwh(int slot)
    {
        var load = BaseLoad[slot];
        for (var k = 0; k < 3; k++) for (var shift = 0; shift < 3; shift++) if (34 + shift + k == slot) load += Oven[k] / 3;
        if (slot is >= 13 and <= 15) load += .05;
        if (slot is 31 or 32) load += .06;
        return Math.Round(load * 1.08 * (slot >= 42 ? 1.08 : 1), 3);
    }
    public static double PvKwh(DateOnly date, int slot) => Math.Round(ClearPvKwh(date, slot) * Cloud(date) * (.9 + .15 * Noise(date.DayNumber, slot, 7)), 3);
    public static double ForecastPvKwh(DateOnly date, int slot) => Math.Round(ClearPvKwh(date, slot) * Cloud(date) * .97, 3);
    public static double CarKwh(DateOnly date, int slot) =>
        slot is >= NightCarFrom and <= NightCarTo ? NightCarKwh : CarEvening(date) && slot is >= EveningCarFrom and <= EveningCarTo ? EveningCarKwh : 0;

    /// <summary>Predbat's state for a slot: overnight charge, charge-then-export, hold and freeze charging, solar into the battery,
    /// freeze export over the peak, a demand-then-export split at 16:10, the export to 60%, hold at the minimum, the car hold on car
    /// evenings, the battery running the evening, and the next night's charge from 23:30.</summary>
    public enum State { Charge, ChargeExport, HoldCharge, FreezeCharge, Demand, FreezeExport, DemandExport, Export, HoldExport, CarHold }
    public static State StateOf(DateOnly date, int slot) => slot switch
    {
        < 9 => State.Charge,
        9 => State.ChargeExport,
        10 => State.HoldCharge,
        < 14 => State.FreezeCharge,
        < 22 => State.Demand,
        < 28 => State.FreezeExport,
        < 32 => State.Demand,
        32 => State.DemandExport,
        < 37 => State.Export,
        37 => State.HoldExport,
        <= EveningCarTo when CarEvening(date) => State.CarHold,
        < 47 => State.Demand,
        _ => State.Charge,
    };

    /// <summary>Battery level at the end of a slot, from its level at the start, the house's net use (load − solar, kWh) and the
    /// overnight charge rate (% per slot).</summary>
    public static double Step(DateOnly date, int slot, double level, double net, double chargeRate)
    {
        var solar = -net / CapacityKwh * 100; // % the house's surplus solar would add (negative when the house needs energy)
        double Clamp(double x) => Math.Round(Math.Clamp(x, ReservePercent, 100), 2);
        return StateOf(date, slot) switch
        {
            State.Charge => Math.Round(Math.Min(ChargeEnd, level + chargeRate), 2),
            State.ChargeExport => MorningTarget,
            State.HoldCharge or State.FreezeCharge or State.HoldExport or State.CarHold => Clamp(level + Math.Max(0, solar)),
            State.Demand => Clamp(level + solar),
            State.FreezeExport => Clamp(level + Math.Min(0, solar)),
            // Until 16:10 the battery runs the house; then it exports evenly so it reaches 60% at 18:30.
            State.DemandExport => ExportPart(Clamp(level + solar / 3), 14 / 3d, 2 / 3d),
            State.Export => ExportPart(level, 37 - slot, 1),
            _ => level,
        };
    }
    static double ExportPart(double level, double slotsLeft, double fraction) =>
        level <= ExportTarget ? level : Math.Round(level - (level - ExportTarget) / slotsLeft * fraction, 2);
    /// <summary>The overnight charge rate (% per slot) that lifts the battery from its 23:30 level to 93% by 04:30 (ten slots).</summary>
    public static double ChargeRate(double levelAt2330) => Math.Round(Math.Max(1, (ChargeEnd - levelAt2330) / 10), 3);

    /// <summary>
    /// Runs slots [<paramref name="from"/>, 48) of a day from <paramref name="levels"/>[from], filling levels[from+1..48]. Returns the
    /// charge rate in force after the day (set by the 23:30 slot). Forecast runs use the forecast load and solar; measured runs use
    /// what the house used and generated.
    /// </summary>
    public static double Run(DateOnly date, int from, double[] levels, double chargeRate, bool forecast)
    {
        for (var slot = from; slot < 48; slot++)
        {
            if (slot == 47) chargeRate = ChargeRate(levels[47]);
            var net = (forecast ? ForecastLoadKwh(slot) - ForecastPvKwh(date, slot) : LoadKwh(date, slot) - PvKwh(date, slot));
            levels[slot + 1] = Step(date, slot, levels[slot], net, chargeRate);
        }
        return chargeRate;
    }

    static readonly ConcurrentDictionary<DateOnly, Day> Days = new();
    /// <summary>The measured day (cached): battery levels and every meter's half-hour energy.</summary>
    public static Day Measured(DateOnly date) => Days.GetOrAdd(date, d => new Day(d));

    /// <summary>The level at local midnight and the overnight charge rate in force then. The evening export always ends at 60% at
    /// 18:30, so the previous evening is replayed from there: no day depends on more than the evening before it.</summary>
    public static (double Level, double ChargeRate) StartOf(DateOnly date)
    {
        var previous = date.AddDays(-1);
        var levels = new double[49]; levels[37] = ExportTarget;
        var rate = Run(previous, 37, levels, 0, forecast: false);
        return (levels[48], rate);
    }

    /// <summary>One measured day: battery levels at each half-hour boundary (49 values), and per slot the house load (excluding the
    /// car), car, solar, battery charge and discharge, and grid import and export (kWh), plus prices.</summary>
    public sealed class Day
    {
        public DateOnly Date { get; }
        public double[] Levels { get; } = new double[49];
        public double ChargeRateBefore { get; }
        public double[] Load { get; } = new double[48];
        public double[] Car { get; } = new double[48];
        public double[] Pv { get; } = new double[48];
        public double[] Charge { get; } = new double[48];
        public double[] Discharge { get; } = new double[48];
        public double[] Import { get; } = new double[48];
        public double[] Export { get; } = new double[48];
        public Day(DateOnly date)
        {
            Date = date;
            (Levels[0], ChargeRateBefore) = StartOf(date);
            Run(date, 0, Levels, ChargeRateBefore, forecast: false);
            for (var i = 0; i < 48; i++)
            {
                Load[i] = LoadKwh(date, i); Car[i] = CarKwh(date, i); Pv[i] = PvKwh(date, i);
                var (charge, discharge, import, export) = Balance(Load[i], Car[i], Pv[i], Levels[i], Levels[i + 1]);
                Charge[i] = charge; Discharge[i] = discharge; Import[i] = import; Export[i] = export;
            }
        }
        /// <summary>A daily counter's value <paramref name="slots"/> half-hours after midnight (0–48), rising linearly through each slot.</summary>
        public static double Counter(double[] perSlot, double slots)
        {
            slots = Math.Clamp(slots, 0, 48);
            var whole = (int)Math.Floor(slots); double total = 0;
            for (var i = 0; i < whole && i < 48; i++) total += perSlot[i];
            if (whole < 48) total += perSlot[whole] * (slots - whole);
            return total;
        }
        /// <summary>Battery level <paramref name="slots"/> half-hours after midnight, interpolated within the slot.</summary>
        public double LevelAt(double slots)
        {
            slots = Math.Clamp(slots, 0, 48);
            var whole = (int)Math.Floor(slots);
            return whole >= 48 ? Levels[48] : Levels[whole] + (Levels[whole + 1] - Levels[whole]) * (slots - whole);
        }
    }

    /// <summary>A slot's battery and grid energy from its levels: the battery moves by (end − start)% of 13.5 kWh, and the grid takes
    /// the rest of load + car + charge − solar − discharge (import when positive, export when negative).</summary>
    public static (double Charge, double Discharge, double Import, double Export) Balance(double load, double car, double pv, double start, double end)
    {
        var battery = (end - start) / 100 * CapacityKwh;
        var net = load + car + Math.Max(0, battery) - pv - Math.Max(0, -battery);
        return (Math.Round(Math.Max(0, battery), 4), Math.Round(Math.Max(0, -battery), 4), Math.Round(Math.Max(0, net), 4), Math.Round(Math.Max(0, -net), 4));
    }

    /// <summary>Deterministic noise in [0, 1) for a day, slot and purpose.</summary>
    static double Noise(int day, int slot, int salt)
    {
        unchecked
        {
            var x = (ulong)(uint)day * 0x9E3779B97F4A7C15UL ^ (ulong)(slot + 1) * 0xC2B2AE3D27D4EB4FUL ^ (ulong)(salt + 7) * 0x165667B19E3779F9UL;
            x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL; x ^= x >> 33;
            return (x >> 11) * (1.0 / (1UL << 53));
        }
    }
}
