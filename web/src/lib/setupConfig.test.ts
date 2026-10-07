import { afterEach, describe, expect, it, vi } from "vitest";
import {
  field,
  foundFromLabel,
  fromEnvironment,
  generateAccessKey,
  meterChanges,
  normaliseAddress,
  saveAndRestart,
  type MeterSuggestion,
  type SetupConfig,
} from "./setupConfig";

const config: SetupConfig = {
  demo: false,
  canSave: true,
  locked: null,
  settingsFile: "/data/settings.json",
  inContainer: true,
  accessKeyNeeded: false,
  restartPending: false,
  fields: [
    {
      key: "Predbat:BaseUrl",
      envVar: "Predbat__BaseUrl",
      kind: "url",
      secret: false,
      value: "http://predbat:5052",
      set: true,
      source: "saved",
      pending: false,
    },
    {
      key: "Ai:ApiKey",
      envVar: "Ai__ApiKey",
      kind: "token",
      secret: true,
      value: null,
      set: true,
      source: "environment",
      pending: false,
    },
  ],
};

describe("setup config", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("generates a 32-character URL-safe access key", () => {
    const key = generateAccessKey((b) => b.fill(255));
    expect(key).toHaveLength(32);
    expect(key).toMatch(/^[A-Za-z0-9_-]+$/);
    expect(generateAccessKey()).not.toEqual(generateAccessKey());
  });

  it("tidies a typed address", () => {
    expect(normaliseAddress(" 192.168.1.20:5052/ ")).toBe("http://192.168.1.20:5052");
    expect(normaliseAddress("https://predbat.example.com")).toBe("https://predbat.example.com");
    expect(normaliseAddress("  ")).toBe("");
  });

  it("finds fields and knows which ones the environment sets", () => {
    expect(field(config, "Predbat:BaseUrl")?.value).toBe("http://predbat:5052");
    expect(field(null, "Predbat:BaseUrl")).toBeUndefined();
    expect(fromEnvironment(config, "Ai:ApiKey")).toBe(true);
    expect(fromEnvironment(config, "Predbat:BaseUrl")).toBe(false);
  });

  it("saves, then waits until Joule has restarted", async () => {
    let started = "2026-10-06T10:00:00Z";
    const calls: string[] = [];
    vi.stubGlobal("sessionStorage", { getItem: () => null, setItem: () => {} });
    vi.stubGlobal("fetch", async (url: string, init?: RequestInit) => {
      calls.push(`${init?.method ?? "GET"} ${url}`);
      if (url === "/api/health") {
        const body = JSON.stringify({ status: "ok", started });
        return new Response(body, { status: 200, headers: { "content-type": "application/json" } });
      }
      started = "2026-10-06T10:00:05Z";
      return new Response(JSON.stringify({ ok: true, restarting: true }), {
        status: 200,
        headers: { "content-type": "application/json" },
      });
    });
    await saveAndRestart({ "Predbat:BaseUrl": "http://predbat:5052" }, { intervalMs: 1, timeoutMs: 1000 });
    expect(calls).toEqual(["GET /api/health", "POST /api/setup/config", "GET /api/health"]);
  });

  it("gives up with advice when Joule doesn't come back", async () => {
    vi.stubGlobal("sessionStorage", { getItem: () => null, setItem: () => {} });
    vi.stubGlobal("fetch", async (url: string) =>
      url === "/api/health"
        ? new Response("{}", { status: 503 })
        : new Response(JSON.stringify({ ok: true }), { status: 200, headers: { "content-type": "application/json" } }),
    );
    await expect(saveAndRestart({}, { intervalMs: 1, timeoutMs: 20 })).rejects.toThrow(/docker compose logs joule/);
  });
});

describe("meter choices", () => {
  const meter = (
    metric: string,
    key: string,
    current: string | null,
    extra: Partial<MeterSuggestion> = {},
  ): MeterSuggestion => ({
    metric,
    key,
    envVar: key.replace(/:/g, "__"),
    current,
    entity: current,
    from: null,
    state: null,
    unit: null,
    alternatives: [],
    ...extra,
  });
  const meters = [
    meter("load", "HomeAssistant:Entities:Load", "sensor.load_today", { auto: true }),
    meter("pv", "HomeAssistant:Entities:Pv", "sensor.pv_today"),
    meter("ev", "HomeAssistant:Entities:Ev", null, { entity: "sensor.zappi_session" }),
    meter("soc", "HomeAssistant:Entities:Soc", null, { entity: null, declined: true }),
    meter("grid_import", "HomeAssistant:Entities:GridImport", "sensor.from_env"),
  ];
  const env = (key: string) => key === "HomeAssistant:Entities:GridImport";

  it("changes nothing while every choice matches what is in use", () => {
    const choice = {
      "HomeAssistant:Entities:Load": "sensor.load_today",
      "HomeAssistant:Entities:Pv": "sensor.pv_today",
    };
    expect(meterChanges(meters, choice, env)).toEqual({});
  });

  it("saves none when a sensor in use is set to Not mapped, so Joule doesn't find it again", () => {
    const choice = {
      "HomeAssistant:Entities:Load": "",
      "HomeAssistant:Entities:Pv": "",
      "HomeAssistant:Entities:GridImport": "",
    };
    expect(meterChanges(meters, choice, env)).toEqual({
      "HomeAssistant:Entities:Load": "none",
      "HomeAssistant:Entities:Pv": "none",
    });
  });

  it("saves a picked sensor, including for a meter left unmapped before, and leaves meters without a choice alone", () => {
    const choice = { "HomeAssistant:Entities:Ev": "sensor.zappi_session", "HomeAssistant:Entities:Soc": "sensor.soc" };
    expect(meterChanges(meters, choice, env)).toEqual({
      "HomeAssistant:Entities:Ev": "sensor.zappi_session",
      "HomeAssistant:Entities:Soc": "sensor.soc",
    });
  });

  it("says where an automatic sensor came from in words", () => {
    expect(foundFromLabel("load_today in apps.yaml")).toBe("Found automatically in Predbat's apps.yaml (load_today)");
    expect(foundFromLabel("name and unit match")).toBe("Found automatically among Predbat's sensors");
    expect(foundFromLabel(null)).toBe("Found automatically from Predbat");
  });
});
