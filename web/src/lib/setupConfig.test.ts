import { afterEach, describe, expect, it, vi } from "vitest";
import {
  field,
  fromEnvironment,
  generateAccessKey,
  normaliseAddress,
  saveAndRestart,
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
