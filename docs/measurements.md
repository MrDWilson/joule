# Measurements

Predbat says what it plans to do. To show what actually happened, Joule reads your Home Assistant energy meters every five minutes and turns the readings into energy per half-hour. Without mapped meters, Joule still shows Predbat's plan, but the "measured" figures and costs stay empty.

## Which sensors to map

The easiest way is **Setup → Map your Home Assistant meters → Find my meters**. It takes the sensors Predbat already uses from its `apps.yaml` (`load_today`, `pv_today`, `import_today`, `export_today`, `soc_percent`, `car_charging_energy` and the Octopus rate sensors) when Predbat's MCP secret is set or `apps.yaml` is mounted, suggests the rest by name and unit, and lets you check each one before saving. The environment variables below do the same and win over Setup.

Map **cumulative energy** sensors: ones whose value keeps growing (kWh, Wh or MWh), usually with `state_class: total_increasing` or `total`. Power sensors (W or kW) are not energy and are rejected rather than guessed.

| Setting | Use |
| --- | --- |
| `HomeAssistant__Entities__Load` | Whole-house consumption. If it includes the car, also map `Ev` so Joule can show "home (excluding car)". |
| `HomeAssistant__Entities__Pv` | Solar generation. |
| `HomeAssistant__Entities__GridImport`, `GridExport` | Grid meter counters (for Octopus, the import and export consumption sensors). |
| `HomeAssistant__Entities__BatteryCharge`, `BatteryDischarge` | Battery energy in and out. |
| `HomeAssistant__Entities__Ev` | Car charger energy. |
| `HomeAssistant__Entities__Soc` | Battery state of charge in %. |
| `HomeAssistant__Entities__ImportTariff`, `ExportTariff` | Current rates, used to cost each half-hour. Octopus `…_current_rate` sensors work. |

Daily counters that reset at midnight, session counters on chargers and lifetime counters all work. Without `HomeAssistant__BaseUrl` and a token, Joule reads these sensors through Predbat's copy of Home Assistant state; with them it reads Home Assistant directly and uses Predbat as a fallback. Readings through Predbat cannot say when the sensor last changed, and Joule labels them as such.

## Gaps, resets and "unknown"

Sensors do not report perfectly, and most blanks have an ordinary cause. Joule's rules:

- **"unknown" is not an outage.** Solar inverters report `unknown` overnight and chargers when no car is plugged in. Joule records that as idle, and counts it as zero where the sensor type means zero.
- **"unavailable" is an outage.** The device or integration is offline. If the counter comes back higher than it went away, the energy used during the outage is known exactly, so Joule spreads it across the gap. It counts in daily totals and costs, but not in slot-by-slot forecast accuracy, and charts draw it fainter.
- **Counter resets** follow Home Assistant's own rule: when a counter drops, the new value is the energy since the reset. Where the sensor reports `last_reset`, the interval is split there. A reset never becomes negative energy.
- **Missing readings** with no way to know the energy stay missing. They never become zero.

The dashboard says how complete each figure is in words, for example "Complete", "Night counted as 0", "≈ estimated", or "Missing 00:00–02:47", and names the meter and the hours. It does not show a percentage when a figure is essentially complete.

## Costs

Net cost is what you paid for imports minus what you earned from exports, each costed half-hour by half-hour from the tariff sensors. Each side shows its own completeness; if either is noticeably incomplete, the figure is marked approximate and names the meter that was offline. Standing charges are not included. A negative net cost is shown as net earnings.

## Plan versus actual

Every plan Predbat publishes is stored unchanged. For each finished half-hour, Joule compares the plan that was current **before** that half-hour started with what the meters measured, so later replans cannot rewrite history. Half-hours rarely line up exactly with meter polls; Joule shares energy from readings that straddle a boundary in proportion to the overlap and labels those figures as estimated. Predbat's own `load_energy_actual` series is never treated as a measurement, because it mixes adjusted load and forecast.

## Alternative forecasts

Joule automatically reads Predbat's `sensor.<prefix>_load_ml_forecast` (LoadML) when Predbat publishes it. You can also map `HomeAssistant__Entities__AlternativeForecast` to any sensor whose attributes declare interval energy:

```json
{
  "forecast_unit": "kWh",
  "forecast_kind": "interval_energy",
  "forecast": [
    { "time": "2026-10-03T01:00:00+01:00", "duration_minutes": 30, "load_kwh": 0.35 }
  ]
}
```

The Plan page compares it with Predbat's forecast on the half-hours both cover and that have complete measurements. Unknown formats are kept for the AI to look at but not scored. A better score is not proof that switching forecasts would save money.
