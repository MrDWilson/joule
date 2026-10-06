import { afterEach, describe, expect, it, vi } from "vitest";
import { buildHash, destinations, parseHash, routeTitle, toHash } from "./router";

describe("section names", () => {
  it("calls Setup's AI tab by the shared word for a run, AI checks", () => {
    expect(destinations.setup.sections.find((s) => s.key === "ai")?.label).toBe("AI checks");
    expect(routeTitle(parseHash("#/setup/ai"))).toContain("AI checks");
  });
});

describe("parseHash", () => {
  it("defaults to Today", () => {
    expect(parseHash("").destination).toBe("today");
    expect(parseHash("#/").hash).toBe("#/today");
    expect(parseHash("#/nowhere").hash).toBe("#/today");
  });
  it("reads destinations, sections and parameters", () => {
    expect(parseHash("#/plan/abc-123")).toMatchObject({ destination: "plan", params: { planId: "abc-123" } });
    expect(parseHash("#/insights/inv/xyz")).toMatchObject({ destination: "insights", params: { id: "xyz" } });
    expect(parseHash("#/insights/suggestions")).toMatchObject({ destination: "insights", section: "suggestions" });
    expect(parseHash("#/setup/changes").section).toBe("changes");
    expect(parseHash("#/setup/bogus").section).toBe("");
  });
  it("keeps the query string", () => {
    const route = parseHash("#/energy?from=2026-10-01&to=2026-10-04");
    expect(route.query.get("from")).toBe("2026-10-01");
    expect(route.hash).toBe("#/energy?from=2026-10-01&to=2026-10-04");
  });
  it("redirects the old page names", () => {
    expect(parseHash("#/overview").hash).toBe("#/today");
    expect(parseHash("#/data").hash).toBe("#/energy");
    expect(parseHash("#/recommendations").hash).toBe("#/insights/suggestions");
    expect(parseHash("#/history").hash).toBe("#/setup/changes");
    expect(parseHash("#/ai%20&%20costs").hash).toBe("#/setup/ai");
    expect(parseHash("#/setup/reports").hash).toBe("#/energy/reports");
  });
});

describe("toHash", () => {
  it("maps old page names and paths", () => {
    expect(toHash("Overview")).toBe("#/today");
    expect(toHash("Investigations")).toBe("#/insights");
    expect(toHash("AI & costs")).toBe("#/setup/ai");
    expect(toHash("Files")).toBe("#/setup/files");
    expect(toHash("/plan")).toBe("#/plan");
    expect(toHash("#/energy/reports")).toBe("#/energy/reports");
  });
});

describe("titles and building", () => {
  it("names the page", () => {
    expect(routeTitle(parseHash("#/insights/suggestions"))).toBe("Suggestions · Insights");
    expect(routeTitle(parseHash("#/insights"))).toBe("Insights");
    expect(routeTitle(parseHash("#/plan"))).toBe("Plan");
  });
  it("builds canonical hashes", () => {
    expect(buildHash("insights", "", { id: "a b" })).toBe("#/insights/inv/a%20b");
    expect(buildHash("energy", "reports")).toBe("#/energy/reports");
  });
});

describe("the primitives gallery", () => {
  afterEach(() => vi.unstubAllEnvs());
  it("opens #/kit in development builds", () => {
    vi.stubEnv("DEV", true);
    expect(parseHash("#/kit").destination).toBe("kit");
  });
  it("sends #/kit to Today in production builds", () => {
    vi.stubEnv("DEV", false);
    const route = parseHash("#/kit");
    expect(route.destination).toBe("today");
    expect(route.hash).toBe("#/today");
  });
});
