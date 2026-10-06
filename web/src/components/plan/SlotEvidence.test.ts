import { describe, expect, it } from "vitest";
import { forecastResult } from "./SlotEvidence";

const slot = (time: string, load: number | null, pv: number | null) => ({
  time,
  durationMinutes: 30,
  actual: { from: "", to: "", metrics: {} },
  estimatedLoadKwh: load,
  estimatedPvKwh: pv,
  estimateMethod: "",
});
const forecast = (time: string, loadForecast: number, pvForecast: number) => ({ time, loadForecast, pvForecast });
const a = "2026-10-05T10:00:00Z",
  b = "2026-10-05T10:30:00Z";

describe("forecastResult", () => {
  const elapsed = [slot(a, 0.54, 0.97), slot(b, 0.54, 0.97)] as never[];
  it("leads with how home use and solar came in against the forecast", () => {
    expect(forecastResult(elapsed, [forecast(a, 0.5, 1), forecast(b, 0.5, 1)])).toBe(
      "Home use came in 8% above forecast; solar 3% below.",
    );
  });
  it("says a big miss as a multiple", () => {
    expect(forecastResult(elapsed, [forecast(a, 0.25, 0.97), forecast(b, 0.25, 0.97)])).toBe(
      "Home use came in at 2.2 times the forecast; solar on forecast.",
    );
  });
  it("leaves out solar when too little was forecast, and says nothing without readings", () => {
    expect(forecastResult(elapsed, [forecast(a, 0.54, 0.05), forecast(b, 0.54, 0.05)])).toBe(
      "Home use came in on forecast.",
    );
    expect(forecastResult([slot(a, null, null)] as never[], [forecast(a, 0.5, 1)])).toBe("");
  });
});
