import { expect, type Page } from "@playwright/test";

/**
 * Joule's navigation: five destinations (Today, Plan, Insights, Energy, Setup), some with sections. Specs open pages
 * by the name a person sees: the destination, or its section tab. A section may list more than one accepted label
 * while its wording settles (the AI tab under Setup).
 */
export const places: Record<string, { destination: string; section?: string | string[]; hash: string }> = {
  Today: { destination: "Today", hash: "#/today" },
  Plan: { destination: "Plan", hash: "#/plan" },
  Insights: { destination: "Insights", hash: "#/insights" },
  Checks: { destination: "Insights", section: "Checks", hash: "#/insights" },
  Suggestions: { destination: "Insights", section: "Suggestions", hash: "#/insights/suggestions" },
  Trials: { destination: "Insights", section: "Trials", hash: "#/insights/experiments" },
  // Energy is one page: the figures, the sensors and the saved reports (#/energy/reports scrolls to them).
  Energy: { destination: "Energy", hash: "#/energy" },
  Reports: { destination: "Energy", hash: "#/energy" },
  Setup: { destination: "Setup", hash: "#/setup" },
  Settings: { destination: "Setup", section: "Predbat settings", hash: "#/setup/settings" },
  "AI checks": { destination: "Setup", section: "AI checks", hash: "#/setup/ai" },
  Sensors: { destination: "Setup", section: "Sensors", hash: "#/setup/sensors" },
  Files: { destination: "Setup", section: "Files", hash: "#/setup/files" },
  Changes: { destination: "Setup", section: "Changes", hash: "#/setup/changes" },
  About: { destination: "Setup", section: "About", hash: "#/setup/about" },
};

const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const labels = (section: string | string[]) => new RegExp(`^(?:${[section].flat().map(escape).join("|")})\\b`);

/**
 * Opens a page the way a person would: the destination in the main navigation (the sidebar on desktop, the bottom
 * tab bar on phones, the cog in the header for Setup on phones), then its section tab. Waits for the page title.
 */
export async function openPage(page: Page, name: string) {
  const place = places[name];
  if (!place) throw new Error(`Unknown page "${name}"`);
  // After a goto or reload the app first shows its connecting screen; wait for the workspace.
  await expect(page.locator("main#main")).toBeAttached({ timeout: 15000 });
  const main = page.getByRole("navigation", { name: "Main navigation" });
  const inMain = main.getByRole("link", { name: new RegExp(`^${escape(place.destination)}\\b`) });
  if (await inMain.count()) await inMain.click();
  else await page.getByRole("link", { name: place.destination, exact: true }).click();
  await expect(page.getByRole("heading", { level: 1, name: place.destination, exact: true })).toBeAttached();
  if (place.section) {
    const tabs = page.getByRole("navigation", { name: `${place.destination} sections` });
    await tabs.getByRole("link", { name: labels(place.section) }).click();
    await expect(tabs.getByRole("link", { name: labels(place.section) })).toHaveAttribute("aria-current", "page");
  }
  await expect(page).toHaveURL(new RegExp(`${escape(place.hash)}(\\?.*)?$`));
}

/** The visible page title, which is the destination: Today, Plan, Insights, Energy or Setup. */
export function pageTitle(page: Page, name: string) {
  return page.getByRole("heading", { level: 1, name: places[name]?.destination ?? name, exact: true });
}
