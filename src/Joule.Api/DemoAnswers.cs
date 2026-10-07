using System.Globalization;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>A scripted demo answer. For a question, Headline (the list row and the "Check finished" banner) is "You asked: …" and
/// Title (the heading over the answer) is the answer in a few words, so the detail never repeats the question; Plain and Summary
/// carry the answer, Evidence its figures. An answer
/// to a question is a "finding" so it is listed, not folded away as "nothing new".</summary>
public sealed record DemoAnswer(string Title, string Headline, string Plain, string Summary, string Verdict, List<string> Evidence);

/// <summary>
/// Answers to Ask Joule in the demo, worked out from the sample house's own meters so they agree with Today, Plan and Energy: yesterday's
/// cost against the week, last night's charge against the plan, and the battery reserve. Anything else is acknowledged by name and told
/// what the demo can answer. No AI is called.
/// </summary>
public static class DemoAnswers
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    static string Gbp(double value) => (value < 0 ? "−£" : "£") + Math.Abs(value).ToString("0.00", Invariant);
    static string Kwh(double value) => value.ToString("0.0", Invariant) + " kWh";
    static string Pct(double value) => Math.Round(value).ToString("0", Invariant) + "%";

    /// <summary>The list title for a question: “You asked: …”, cut to fit a list row.</summary>
    public static string AskedTitle(string question) => $"You asked: “{InvestigationQuality.Shorten(question.Trim(), 64)}”";

    public static DemoAnswer Answer(string? question, DataStore db, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new("A fresh look at your sample energy day", "Sample day checked",
                "The sample plan charges overnight and exports in the late afternoon, as expected. This is a scripted demonstration, not an AI answer.",
                "Reviewed the demonstration plan, configuration and previous decisions. The plan charges in the cheap overnight window and exports to 60% between 16:10 and 18:30. This is a scripted demonstration, not an AI response.",
                "no_change", ["Source: synthetic demonstration data.", "No live measurements or verified savings are available."]);
        var asked = AskedTitle(question);
        var q = question.ToLowerInvariant();
        try
        {
            if (Regex.IsMatch(q, @"\breserve\b")) return Reserve(asked, db, zone, now);
            if (Regex.IsMatch(q, @"last night|overnight|\bcharge\b|charging")) return LastNight(asked, db, zone, now);
            if (Regex.IsMatch(q, @"yesterday|expensive|\bcost|spend|bill")) return Yesterday(asked, db, zone, now);
        }
        catch (Exception e) when (e is DomainException or InvalidOperationException or KeyNotFoundException) { }
        return new("Demo answers cover three sample questions", asked, "In the demo Joule answers three sample questions from the sample house's meters: yesterday's cost, last night's charge and the battery reserve. Connect your Predbat and an AI service to ask anything.",
            $"You asked: “{question.Trim()}”. This is the demo, so no AI is connected and Joule can't look into that. It can answer three sample questions from the sample house's meters: why yesterday cost what it did, whether last night's charge went to plan, and whether the battery reserve is right. Connect your Predbat and an AI service to ask your own.",
            "finding", ["Scripted demo answer; no AI was used."]);
    }

    static DateTimeOffset Midnight(DateTimeOffset at, TimeZoneInfo zone, int days = 0) => CivilTime.FirstValidInstant(TimeZoneInfo.ConvertTime(at, zone).Date.AddDays(days), zone);

    static DemoAnswer Yesterday(string asked, DataStore db, TimeZoneInfo zone, DateTimeOffset now)
    {
        var today = Midnight(now, zone); var yesterday = Midnight(now, zone, -1); var weekAgo = Midnight(now, zone, -8);
        var day = db.ReadEnergySummary(yesterday, today);
        var week = db.ReadEnergySummary(weekAgo, yesterday);
        if (StandingCharge.HeadlineNet(day) is not { } cost || StandingCharge.HeadlineNet(week) is not { } weekCost) throw new InvalidOperationException();
        var standingText = day.StandingChargeIncluded && day.StandingChargeGbp is { } standing && day.StandingChargePencePerDay is { } rate
            ? $"net cost includes the {Gbp(standing)} standing charge ({rate.ToString("0.##", Invariant)}p a day)."
            : "costs leave out the standing charge.";
        var average = weekCost / 7;
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(yesterday, zone).Date);
        var house = DemoHouse.Measured(date);
        var eveningCar = house.Car.Skip(DemoHouse.EveningCarFrom).Take(DemoHouse.EveningCarTo - DemoHouse.EveningCarFrom + 1).Sum();
        var solar = day.Metrics.TryGetValue("pv", out var pv) ? pv.EnergyKwh ?? 0 : 0;
        var dayName = TimeZoneInfo.ConvertTime(yesterday, zone).ToString("dddd", Invariant);
        var difference = cost - average;
        var why = eveningCar > 0
            ? $"The car topped up {Kwh(eveningCar)} between 19:00 and 20:30 at 25.4p while the battery was held for it, about {Gbp(eveningCar * .254)} on its own."
            : "There was no evening car charge, so the evening ran on the battery and almost nothing was bought at the 25.4p peak price.";
        var net = cost < 0 ? $"earned {Gbp(-cost)} net" : $"cost {Gbp(cost)} net";
        var averageText = $"the 7-day average of {(average < 0 ? $"{Gbp(-average)} earned" : Gbp(average))} a day";
        var plain = Math.Abs(difference) < .15 ? $"{dayName} {net}, about the same as {averageText}. {why}"
            : difference > 0 ? $"{dayName} {net}, {Gbp(difference)} more than {averageText}. {why}"
            : $"{dayName} wasn't expensive: it {net}, {Gbp(-difference)} better than {averageText}. {why}";
        var summary = $"{plain} Solar made {Kwh(solar)}. Bought {Gbp(day.ImportCostGbp ?? 0)}, sold {Gbp(day.ExportCreditGbp ?? 0)}; {standingText}";
        var heading = Math.Abs(difference) < .15 ? $"{dayName} cost about the same as the week's average" : difference > 0 ? $"{dayName} cost {Gbp(difference)} more than the week's average" : $"{dayName} cost less than the week's average";
        return new(heading, asked, InvestigationQuality.Shorten(plain, 280), summary, "finding",
        [
            $"{dayName} (meters): bought {Gbp(day.ImportCostGbp ?? 0)}, sold {Gbp(day.ExportCreditGbp ?? 0)}{(day.StandingChargeIncluded && day.StandingChargeGbp is { } sc ? $", standing charge {Gbp(sc)}" : "")}, net {Gbp(cost)}.",
            $"Previous 7 days: net {Gbp(weekCost)}, {Gbp(average)} a day.",
            eveningCar > 0 ? $"Evening car charge: {Kwh(eveningCar)} at 25.4p/kWh." : "No evening car charge.",
            "Scripted demo answer from the sample house's meters; no AI was used.",
        ]);
    }

    static DemoAnswer LastNight(string asked, DataStore db, TimeZoneInfo zone, DateTimeOffset now)
    {
        var end = Midnight(now, zone).AddHours(5.5);
        if (end > now) end = Midnight(now, zone, -1).AddHours(5.5);
        var start = end.AddHours(-6);
        var night = db.ReadEnergySummary(start, end);
        var bought = night.Metrics["grid_import"].EnergyKwh ?? 0; var cost = night.ImportCostGbp ?? 0;
        var charged = night.Metrics["battery_charge"].EnergyKwh ?? 0; var car = night.Metrics.TryGetValue("ev", out var ev) ? ev.EnergyKwh ?? 0 : 0;
        var from = DemoTelemetry.Readings(start, zone)["soc"] ?? 0; var to = DemoTelemetry.Readings(end, zone)["soc"] ?? 0;
        var average = bought > 0 ? cost * 100 / bought : 0;
        var onPlan = to >= DemoHouse.MorningTarget - 1;
        var plain = $"{(onPlan ? "Yes." : "Not quite.")} The plan was to charge to 94% by 04:30, then export down to a 90% target. The battery went from {Pct(from)} at 23:30 to {Pct(to)} at 05:30, taking {Kwh(charged)} at an average {average.ToString("0.0", Invariant)}p/kWh.";
        var summary = $"{plain} In all the house bought {Kwh(bought)} overnight ({Gbp(cost)}), {Kwh(car)} of it for the car. Every unit was bought in the cheap 23:30–05:30 window.";
        return new(onPlan ? "Last night's charge went to plan" : "Last night's charge fell short of plan", asked, InvestigationQuality.Shorten(plain, 280), summary, onPlan ? "finding" : "problem",
        [
            $"Battery {Pct(from)} at 23:30 → {Pct(to)} at 05:30; planned target 94%, then 90% from 05:00.",
            $"Battery charged {Kwh(charged)}; grid import {Kwh(bought)} costing {Gbp(cost)} ({average.ToString("0.0", Invariant)}p/kWh average).",
            "Scripted demo answer from the sample house's meters; no AI was used.",
        ]);
    }

    static DemoAnswer Reserve(string asked, DataStore db, TimeZoneInfo zone, DateTimeOffset now)
    {
        var lowest = double.MaxValue; var lowestAt = now;
        for (var d = 1; d <= 7; d++)
        {
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(-d));
            var levels = DemoHouse.Measured(date).Levels;
            for (var i = 0; i < 48; i++) if (levels[i] < lowest) { lowest = levels[i]; lowestAt = DemoHouse.Midnight(date, zone).AddMinutes(30 * i); }
        }
        var local = TimeZoneInfo.ConvertTime(lowestAt, zone);
        var headroom = (lowest - DemoHouse.ReservePercent) / 100 * DemoHouse.CapacityKwh;
        var plain = $"Yes for saving money. The reserve is 4% and the lowest the battery went in the last 7 days was {Pct(lowest)} ({local.ToString("ddd HH:mm", Invariant)}), so it never held the battery back. Raise it only if you want power kept for a power cut.";
        var summary = $"{plain} The cheap overnight charge always starts with {Kwh(headroom)} or more above the reserve. Raising the reserve to 20% would keep 2.7 kWh back for an outage, and the evenings would then buy a little more at 25.4p.";
        return new("The 4% reserve is fine for saving money", asked, InvestigationQuality.Shorten(plain, 280), summary, "finding",
        [
            "set_reserve_min = 4% (0.5 kWh of 13.5 kWh).",
            $"Lowest measured battery level in 7 days: {Pct(lowest)} at {local.ToString("ddd d MMM HH:mm", Invariant)}.",
            "Scripted demo answer from the sample house's meters; no AI was used.",
        ]);
    }
}
