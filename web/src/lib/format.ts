/**
 * Every number Joule shows goes through here, so one figure always reads the same way on every page.
 *
 * Rules: en-GB grouping ("94,317.7"), a real minus sign (U+2212) rather than a hyphen, kWh to 1 dp in tiles and 2 dp in
 * tables and tooltips, prices to at most 2 dp with trailing zeros trimmed, and one wording for every kind of missing value.
 */

/** The typographic minus sign. */
export const MINUS = "−";

/** Why a figure is missing, in plain words. Use these instead of inventing a new phrase. */
export const MISSING = {
  "not-measured": "Not measured",
  "sensor-offline": "Sensor offline",
  "sensor-idle": "Sensor idle",
  "not-priced": "Not priced",
  "not-set-up": "Not set up",
} as const;
export type MissingReason = keyof typeof MISSING;

/** The quiet placeholder for a figure that isn't there and needs no explanation. */
export const DASH = "—";

type Value = number | null | undefined;
interface Common {
  /** What to show for a missing value: one of the MISSING reasons, or a dash by default. */
  missing?: MissingReason;
}

const finite = (n: Value): n is number => typeof n === "number" && Number.isFinite(n);
const absent = (options?: Common) => (options?.missing ? MISSING[options.missing] : DASH);

const formatters = new Map<string, Intl.NumberFormat>();
function numberFormat(min: number, max: number) {
  const key = `${min}-${max}`;
  let f = formatters.get(key);
  if (!f) {
    f = new Intl.NumberFormat("en-GB", { minimumFractionDigits: min, maximumFractionDigits: max });
    formatters.set(key, f);
  }
  return f;
}

/** Rounds first so that "−0.0" never appears. */
function signed(n: number, digits: (abs: number) => string, sign: "auto" | "always") {
  const text = digits(Math.abs(n));
  const zero = /^[0.,]+$/.test(text.replace(/[^\d.,]/g, "")) && !/[1-9]/.test(text);
  if (zero) return text;
  if (n < 0) return MINUS + text;
  return sign === "always" ? `+${text}` : text;
}

/** A plain number with grouping: number(94317.687, 1) → "94,317.7". */
export function number(n: Value, dp = 0, options?: Common & { signed?: boolean }) {
  if (!finite(n)) return absent(options);
  return signed(n, (a) => numberFormat(dp, dp).format(a), options?.signed ? "always" : "auto");
}

/** A count with grouping: count(1234567) → "1,234,567". */
export function count(n: Value, options?: Common) {
  return number(finite(n) ? Math.round(n) : n, 0, options);
}

/** A large count shortened: compact(1_940_000) → "1.9M", compact(12_400) → "12.4k". */
export function compact(n: Value, options?: Common) {
  if (!finite(n)) return absent(options);
  const a = Math.abs(n);
  const [value, unit] = a >= 1e9 ? [n / 1e9, "B"] : a >= 1e6 ? [n / 1e6, "M"] : a >= 1e3 ? [n / 1e3, "k"] : [n, ""];
  return number(value, unit && Math.abs(value) < 100 ? 1 : 0).replace(/\.0(?=$)/, "") + unit;
}

export type EnergyPrecision = "tile" | "table";
/**
 * Energy: kwh(12.345) → "12.3 kWh" for tiles and headlines, kwh(12.345, { precision: "table" }) → "12.35 kWh" for tables
 * and tooltips. unit: false leaves the unit off for layouts that set it separately.
 */
export function kwh(n: Value, options?: Common & { precision?: EnergyPrecision; unit?: boolean; signed?: boolean }) {
  if (!finite(n)) return absent(options);
  const dp = options?.precision === "table" ? 2 : 1;
  const text = number(n, dp, { signed: options?.signed });
  return options?.unit === false ? text : `${text} kWh`;
}

/** Power: kw(3.84) → "3.8 kW"; below 0.1 kW two decimals keep a small draw visible ("0.05 kW"). */
export function kw(n: Value, options?: Common & { unit?: boolean }) {
  if (!finite(n)) return absent(options);
  const dp = n !== 0 && Math.abs(n) < 0.1 ? 2 : 1;
  const text = number(n, dp);
  return options?.unit === false ? text : `${text} kW`;
}

/** Money in pounds: gbp(1.1) → "£1.10", gbp(-0.25) → "−£0.25", gbp(0.25, { signed: true }) → "+£0.25". */
export function gbp(n: Value, options?: Common & { signed?: boolean }) {
  if (!finite(n)) return absent(options);
  return signed(n, (a) => `£${numberFormat(2, 2).format(a)}`, options?.signed ? "always" : "auto");
}

/**
 * A unit price in pence, at most 2 dp with trailing zeros trimmed: pence(24.123456) → "24.12p/kWh", pence(15.0049) →
 * "15p/kWh", pence(-2.5) → "−2.5p/kWh". unit: "p" gives "24.12p" for columns that already say per kWh.
 */
export function pence(n: Value, options?: Common & { unit?: "p/kWh" | "p" }) {
  if (!finite(n)) return absent(options);
  return `${number(n, 0) === number(n, 2) ? number(n, 0) : trimZeros(number(n, 2))}${options?.unit ?? "p/kWh"}`;
}
function trimZeros(text: string) {
  return text.includes(".") ? text.replace(/\.?0+$/, "") : text;
}

/**
 * A percentage. percent(74.05) → "74%" for values already in percent; percent(0.893, { fraction: true }) → "89%".
 * round: "floor" never rounds a shortfall up to 100% (use it for coverage).
 */
export function percent(n: Value, options?: Common & { fraction?: boolean; dp?: number; round?: "nearest" | "floor" }) {
  if (!finite(n)) return absent(options);
  const dp = options?.dp ?? 0;
  let value = options?.fraction ? n * 100 : n;
  if (options?.round === "floor") value = Math.floor(value * 10 ** dp) / 10 ** dp;
  return `${number(value, dp)}%`;
}

/** US dollars for AI cost estimates: usd(0.01234) → "$0.0123", usd(3.5) → "$3.50", usd(0) → "$0.00". */
export function usd(n: Value, options?: Common) {
  if (!finite(n)) return absent(options);
  const a = Math.abs(n);
  const dp = a === 0 || a >= 1 ? 2 : 4;
  return signed(n, (x) => `$${numberFormat(dp === 4 ? 2 : 2, dp).format(x)}`, "auto");
}

/** "Fully measured", "89% measured" (rounded down, so a gap never reads as 100%) or "No readings". */
export function coverageText(n: Value) {
  if (!finite(n) || n <= 0) return "No readings";
  if (n >= 0.999999) return "Fully measured";
  return `${Math.max(1, Math.floor(n * 100))}% measured`;
}
