import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

// axe (WCAG 2.1 AA) on every page in demo mode, at desktop and phone widths: no serious or critical violations.
test.use({ timezoneId: "Europe/London" });
test.describe.configure({ timeout: 240_000 });

const routes = [
  "#/today",
  "#/plan",
  "#/insights",
  "#/insights/suggestions",
  "#/insights/experiments",
  "#/energy",
  "#/energy/reports",
  "#/setup",
  "#/setup/settings",
  "#/setup/ai",
  "#/setup/sensors",
  "#/setup/notifications",
  "#/setup/files",
  "#/setup/changes",
  "#/setup/about",
];

for (const width of [1440, 390]) {
  test(`every page has no serious accessibility violations at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: width < 700 ? 844 : 900 });
    const problems: string[] = [];
    for (const route of routes) {
      await page.goto(`/${route}`);
      await expect(page.locator("main#main")).toBeVisible();
      await page.waitForLoadState("networkidle").catch(() => {});
      await page.waitForTimeout(400);
      const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
      for (const v of results.violations.filter((v) => v.impact === "serious" || v.impact === "critical"))
        problems.push(
          `${route} · ${v.id} (${v.impact}): ${v.nodes
            .slice(0, 3)
            .map((n) => n.target.join(" "))
            .join(" | ")}`,
        );
    }
    expect(problems).toEqual([]);
  });
}
