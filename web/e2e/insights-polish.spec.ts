import { test, expect, type Page } from "@playwright/test";
import { fetchFresh, fulfillRewritten } from "./support/routes";

/*
 * The Insights polish pass: honest money chips, safe file edits, quick replies that never dismiss, one place to type,
 * undone suggestions, Ask Joule that asks in one tap, and an AI checks page whose parts agree with each other.
 */
test.use({ timezoneId: "Europe/London" });
test.afterEach(async ({ page }) => page.unrouteAll({ behavior: "ignoreErrors" }));

type Payload = any; // eslint-disable-line @typescript-eslint/no-explicit-any
async function rewriteState(page: Page, edit: (payload: Payload) => Payload) {
  await page.route("**/api/state", async (route) => {
    const response = await fetchFresh(route);
    await fulfillRewritten(route, response, edit(await response.json()));
  });
}
async function open(page: Page, hash = "#/insights") {
  await page.addInitScript(() => {
    sessionStorage.setItem("joule.setupAutoOpened", "1");
    localStorage.setItem("joule.firstRunSeen", "1");
  });
  await page.goto(`/${hash}`);
  await expect(page.locator("main#main")).toBeAttached({ timeout: 15000 });
}
const exportCheck = (p: Payload) =>
  p.state.investigations.find((i: Payload) => /^Predbat can't see today's export/.test(i.title));
const ago = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

test("a file edit with a hidden credential never offers broken YAML to copy, and a replacement says Replace", async ({
  page,
}) => {
  await rewriteState(page, (p) => {
    const change = exportCheck(p).fileChanges[0];
    Object.assign(change, {
      summary: "Replace the two inline credentials with Home Assistant secret references.",
      location: "Replace the existing top-level ha_key and mcp_secret entries.",
      snippet: "ha_key: !secret predbat_ha_key\n[redacted]",
    });
    return p;
  });
  await open(page, "#/insights/suggestions");
  const card = page.getByRole("article", { name: /Replace the two inline credentials/ });
  await expect(card.locator("figcaption")).toHaveText("Replace in apps.yaml");
  await expect(card.getByRole("button", { name: "Copy snippet" })).toHaveCount(0);
  await expect(card).toContainText(
    "Part of this edit was hidden for safety. Open apps.yaml and replace the values by hand.",
  );
  // With nothing to copy, marking it applied is the card's main action.
  await expect(card.getByRole("button", { name: "Mark as applied" })).toHaveClass(/button-primary/);
});

test("the money chip is measured against the plan and never contradicts the headline", async ({ page }) => {
  await rewriteState(page, (p) => {
    const check = exportCheck(p);
    Object.assign(check, { headline: "Exporting 6.82 kWh lost about 67p", impactPence: -107 });
    const opportunity = p.state.investigations.find((i: Payload) => i.verdict === "opportunity");
    Object.assign(opportunity, { impactPence: -250 });
    return p;
  });
  await open(page);
  const main = page.locator("main");
  await expect(main).not.toContainText(/\bsaved\b/);
  const lost = page.locator(".run-row").filter({ hasText: "lost about 67p" });
  await expect(lost.locator(".run-chips")).not.toContainText(/plan/);
  await expect(page.locator(".run-row").filter({ hasText: "Evening load" })).toContainText("£2.50 under plan");
});

test("quick replies follow the answer: Thanks only acknowledges, and I'll do it is for to-dos", async ({ page }) => {
  const dismissed: string[] = [];
  await page.route("**/api/investigations/*/dismiss", (route) => {
    dismissed.push(route.request().url());
    return route.fulfill({ json: { ok: true } });
  });
  await rewriteState(page, (p) => {
    const check = exportCheck(p);
    check.thread = [
      { at: ago(5), role: "user", text: "Why does this matter?" },
      { at: ago(4), role: "ai", text: "Without it the plan and the meters cannot be compared.", verdict: "answer" },
    ];
    check.nextSteps[0].thread = [
      { at: ago(5), role: "user", text: "How do I check it?" },
      { at: ago(4), role: "ai", text: "Start a short charge and watch the sensor.", verdict: "answer" },
    ];
    return p;
  });
  await open(page);
  await page.locator(".run-row").filter({ hasText: "Predbat can't see today's export" }).getByRole("link").click();
  const detail = page.getByRole("article", { name: /Predbat can't see today's export/ });
  const checkReply = detail.getByRole("region", { name: /^Reply to:/ });
  const quick = checkReply.getByRole("group", { name: "Quick replies" });
  await expect(quick.getByRole("button", { name: "I'll do it" })).toHaveCount(0);
  await expect(quick.getByRole("button", { name: "Still disagree" })).toHaveCount(0);
  await quick.getByRole("button", { name: "Thanks", exact: true }).click();
  await expect(checkReply.getByRole("status")).toHaveText("Noted.");
  await expect(checkReply.getByRole("group", { name: "Quick replies" })).toHaveCount(0);
  expect(dismissed).toEqual([]);

  const todo = detail.getByRole("article", { name: "Find your inverter's daily export sensor in Home Assistant" });
  await expect(
    todo.getByRole("group", { name: "Quick replies" }).getByRole("button", { name: "I'll do it" }),
  ).toBeVisible();
  await expect(
    todo.getByRole("group", { name: "Quick replies" }).getByRole("button", { name: "Ask something else" }),
  ).toBeVisible();
});

test("one place to type: a card's reply hides the check's Reply to Joule, and Done sends no note", async ({ page }) => {
  const notes: unknown[] = [];
  await page.route("**/api/investigations/*/followups/*/dismiss", (route) => {
    notes.push(route.request().postDataJSON());
    return route.fulfill({ json: { ok: true } });
  });
  await page.setViewportSize({ width: 1280, height: 900 });
  await open(page);
  await page.locator(".run-row").filter({ hasText: "Predbat can't see today's export" }).getByRole("link").click();
  const detail = page.getByRole("article", { name: /Predbat can't see today's export/ });
  const main = detail.getByRole("button", { name: "Reply to Joule" });
  await expect(main).toBeVisible();
  const todo = detail.getByRole("article", { name: "Find your inverter's daily export sensor in Home Assistant" });
  // Card replies are quiet ghost buttons; the check's own reply is the one prominent one.
  await expect(todo.getByRole("button", { name: "Reply", exact: true })).toHaveClass(/button-ghost/);
  await todo.getByRole("button", { name: "Reply", exact: true }).click();
  await expect(todo.getByRole("textbox", { name: "Your reply" })).toBeVisible();
  await expect(main).toHaveCount(0);
  await todo.getByRole("button", { name: "Cancel" }).click();
  await expect(detail.getByRole("button", { name: "Reply to Joule" })).toBeVisible();
  await todo.getByRole("button", { name: "Done" }).click();
  await expect.poll(() => notes.length).toBe(1);
  expect(notes[0] ?? {}).not.toHaveProperty("note");
});

test("a suggestion that was applied and then undone says so and can be reopened", async ({ page }) => {
  const reopened: string[] = [];
  await page.route("**/api/proposals/*/reopen", (route) => {
    reopened.push(route.request().url());
    return route.fulfill({ json: { ok: true } });
  });
  await rewriteState(page, (p) => {
    p.state.proposals = p.state.proposals.map((x: Payload) => ({
      ...x,
      status: "Reverted",
      decidedAt: ago(30),
      decisionNote: "Done",
      thread: [],
    }));
    return p;
  });
  await open(page, "#/insights/suggestions?view=closed");
  const row = page.locator(".closed-row").filter({ hasText: "Bring the evening load forecast closer to reality" });
  await expect(row).toContainText("Setting change · Applied, then undone · Today");
  await expect(row).not.toContainText("Your note");
  await row.getByRole("button", { name: "Reopen" }).click();
  await expect.poll(() => reopened.length).toBe(1);

  // On the check's page the empty "For you" reads as a sentence, not a bare "Closed 1".
  await open(page);
  await page.locator(".run-row").filter({ hasText: "Evening load" }).getByRole("link").click();
  const forYou = page.getByRole("region", { name: "What this check left for you" });
  await expect(forYou).toContainText("You applied this suggestion, then undid it.");
  await expect(forYou.getByRole("button", { name: /^Closed/ })).toHaveCount(0);
  await expect(page.locator(".detail-meta")).toContainText(/Finding: \w+ confidence/);
});

test("a suggested question asks straight away, and the banner repeats it", async ({ page }) => {
  await open(page);
  const ask = page.getByRole("region", { name: "Ask Joule" });
  await ask.getByRole("button", { name: "Is my battery reserve right?" }).click();
  const done = page.getByRole("region", { name: "Check finished" });
  await expect(done).toBeVisible({ timeout: 20000 });
  await expect(done).toContainText("Answer to “Is my battery reserve right?”");
  await expect(page.getByRole("status").filter({ hasText: "Asked. Joule is checking." })).toHaveCount(0);
});

test("with a check open beside the list, Ask Joule is one line and Recent checks starts in the first view", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await open(page);
  await page.locator(".run-row").filter({ hasText: "Evening load" }).getByRole("link").click();
  const ask = page.getByRole("region", { name: "Ask Joule" });
  await expect(ask.getByRole("textbox", { name: "Your question" })).toBeVisible();
  await expect(ask.getByRole("group", { name: "Suggested questions" })).toHaveCount(0);
  await expect(ask.getByRole("button", { name: /Choose the time/ })).toHaveCount(0);
  await expect(page.getByRole("button", { name: /What Joule knows about your home/ })).toHaveCount(0);
  const heading = (await page.getByRole("heading", { name: "Recent checks" }).boundingBox())!;
  expect(heading.y).toBeLessThan(900);
});

test("a check that didn't finish has its own marker, apart from quiet checks", async ({ page }) => {
  await open(page);
  const failed = page.locator(".run-row.is-unfinished").first();
  await expect(failed.locator(".run-alert")).toBeVisible();
  await expect(failed.locator(".run-dot")).toHaveCount(0);
  await expect(failed.locator(".run-kind")).toHaveText("Didn't finish");
});

test("the demo AI checks page agrees with itself: sample answers, no token figures, schedule controls together", async ({
  page,
}) => {
  // With automatic checks off (the demo starts with them on), the triggers fold away and the limits are disabled.
  await rewriteState(page, (p) => {
    p.state.ai.scheduled = false;
    if (p.ai?.schedule) p.ai.schedule.enabled = false;
    return p;
  });
  await open(page, "#/setup/ai");
  const provider = page.getByRole("region", { name: "AI provider" });
  await expect(provider.locator(".chip")).toHaveText("Demo · sample answers");
  await expect(provider.getByRole("textbox", { name: "Model" })).toHaveCount(0);
  const summary = page.getByRole("region", { name: "Today" });
  await expect(summary).not.toContainText("Tokens");
  await expect(page.getByRole("figure", { name: "Tokens per day" })).toHaveCount(0);
  const runs = page.getByRole("region", { name: "Recent demo checks" });
  await expect(runs.locator(".usage-row").first()).toContainText("Demo check · sample answer");
  await expect(runs.locator(".usage-tokens")).toHaveCount(0);
  const schedule = page.getByRole("region", { name: "When Joule checks" });
  await expect(schedule.getByRole("switch", { name: "Automatic checks" })).toBeVisible();
  await expect(schedule.getByText("When turned on, Joule checks:")).toBeVisible();
  await expect(schedule.getByLabel("Most AI checks a day")).toBeDisabled();
  // Provider and schedule stack on the left, aim and live tools on the right.
  const left = (await provider.boundingBox())!,
    right = (await page.getByRole("region", { name: "What checks aim for" }).boundingBox())!;
  expect(right.x).toBeGreaterThan(left.x + left.width - 1);
});
