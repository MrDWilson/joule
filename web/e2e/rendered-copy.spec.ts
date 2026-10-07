import { test, expect, type Page } from "@playwright/test";

/**
 * Rendered-copy lint. Walks every route, in demo mode and with live-shaped fixtures that deliberately contain Predbat
 * codes, backticks, snake_case keys, entity ids and UTC timestamps in AI text, plan slots and closure reasons, and
 * fails on anything internal that reaches the screen. This is the check for "I also see FrzChg etc".
 *
 * Code chips (<code>, <pre>, <kbd>) are allowed to show identifiers; everything else must read as plain English.
 */
test.use({ timezoneId: "Europe/London" });
test.describe.configure({ timeout: 180_000 });

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

const rules: { name: string; pattern: RegExp }[] = [
  {
    name: "Predbat plan code",
    pattern: /(?<![\w.])(FrzExp|FrzChrg|FrzChg|HoldChrg|HoldChg|HoldExp|NoChrg|Chrg)(?![\w])/,
  },
  { name: "'Predbat state' prefix", pattern: /Predbat state (?!\()/ },
  { name: "raw backtick", pattern: /`/ },
  {
    name: "entity id outside a code chip",
    pattern: /\b(?:sensor|select|binary_sensor|switch|input_number|input_boolean)\.[a-z0-9_]+/,
  },
  { name: "UTC", pattern: /\bUTC\b/ },
  { name: "ISO timestamp", pattern: /\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?Z\b/ },
  { name: "ChatGpt", pattern: /\bChatGpt\b/ },
  { name: "telemetry", pattern: /telemetry/i },
  { name: "Failed to fetch", pattern: /Failed to fetch/ },
  { name: "snake_case key", pattern: /(?<![\w./-])[a-z]+(?:_[a-z0-9]+)+(?![\w./-])/ },
];

/**
 * Known gaps, so this test still guards everything else today. Each entry says why it's allowed; delete it once the page
 * is fixed and the test proves it clean.
 */
const knownGaps: { route: RegExp; rule: string; reason: string; text: RegExp }[] = [
  // The automatic-check reason comes from the server, which still counts the daily budget in UTC.
  { route: /^#\/(today|setup\/ai)$/, rule: "UTC", reason: "server budget wording", text: /resets at midnight UTC/ },
];

/** Every text node a person (or a screen reader) could meet, except inside code chips. */
async function visibleCopy(page: Page) {
  return page.evaluate(() => {
    const skip = (el: Element | null) =>
      !!el?.closest("code, pre, kbd, script, style, noscript, svg, template, [data-raw]");
    const texts: string[] = [];
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
      const t = (n.textContent ?? "").trim();
      if (t && !skip(n.parentElement)) texts.push(t);
    }
    // Accessible names and descriptions are read aloud too.
    for (const el of Array.from(document.querySelectorAll("[aria-label], [title], [placeholder]"))) {
      if (skip(el)) continue;
      for (const attr of ["aria-label", "title", "placeholder"]) {
        const v = el.getAttribute(attr);
        if (v) texts.push(`\u0000${v}`);
      }
    }
    return texts;
  });
}

/** Opens the notifications bell and the status chip one at a time and returns the text inside each panel. */
async function popoverCopy(page: Page) {
  const texts: string[] = [];
  for (const [trigger, name] of [
    [/^Notifications/, "Notifications"],
    [/^Status: /, "Connection status"],
  ] as const) {
    await page.getByRole("button", { name: trigger }).first().click();
    const panel = page.getByRole("dialog", { name });
    await expect(panel).toBeVisible();
    texts.push(
      ...(await panel.evaluate((root) => {
        const out: string[] = [];
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        for (let n = walker.nextNode(); n; n = walker.nextNode()) {
          const t = (n.textContent ?? "").trim();
          if (t && !n.parentElement?.closest("code, pre, kbd, svg")) out.push(t);
        }
        return out;
      })),
    );
    await page.keyboard.press("Escape");
    await expect(panel).toBeHidden();
  }
  expect(texts.length).toBeGreaterThan(0);
  return texts;
}

async function lint(page: Page, label: string) {
  const found: string[] = [];
  for (const route of routes) {
    await page.goto(`/${route}`);
    await expect(page.locator("main#main")).toBeVisible();
    await page.waitForLoadState("networkidle").catch(() => {});
    await page.waitForTimeout(500);
    // Open every disclosure so collapsed text is checked as well.
    await page.evaluate(() =>
      document.querySelectorAll("details").forEach((d) => ((d as HTMLDetailsElement).open = true)),
    );
    // Some disclosures load their content when opened (a saved report asks the server for its figures).
    await page.waitForLoadState("networkidle").catch(() => {});
    await page.waitForTimeout(300);
    const texts = await visibleCopy(page);
    // The shell's popovers (notifications, connection status) are closed while the page is walked: open each one in
    // turn on the first route and check its text too. Their content is the same on every page.
    if (route === routes[0]) texts.push(...(await popoverCopy(page)));
    for (const rule of rules) {
      const hits = texts.filter((raw) => {
        // Accessible names may carry a setting key (that is how a control is named); all other rules apply to them.
        const attribute = raw.startsWith("\u0000");
        const text = raw.replace(/^\u0000/, "");
        if (attribute && rule.name === "snake_case key") return false;
        if (knownGaps.some((g) => g.route.test(route) && (g.rule === rule.name || g.rule === "*") && g.text.test(text)))
          return false;
        return rule.pattern.test(text);
      });
      for (const hit of hits.slice(0, 3)) found.push(`${label} ${route} · ${rule.name}: "${hit.slice(0, 140)}"`);
    }
  }
  return found;
}

test("demo pages never show internal codes, keys or UTC", async ({ page }) => {
  expect(await lint(page, "demo")).toEqual([]);
});

const HALF_HOUR = 1800000;

/** Live-shaped data (synthetic ids) with every kind of internal text the AI and Predbat have produced. */
async function liveShaped(page: Page) {
  const payload = await (await page.request.get("/api/state")).json();
  const start = Math.ceil(Date.now() / HALF_HOUR) * HALF_HOUR;
  const actions = ["FrzExp", "FrzChg", "HoldChrg", "Chrg 70%", "Hold for car", "Chrg", "Exp", "Demand"];
  payload.plan = {
    id: "lint-plan",
    at: new Date().toISOString(),
    collectedAt: new Date().toISOString(),
    source: "Predbat",
    slots: actions.map((action, i) => ({
      time: new Date(start + i * HALF_HOUR).toISOString(),
      durationMinutes: 30,
      action,
      loadForecast: 0.42,
      loadActual: null,
      pvForecast: 0,
      pvActual: null,
      socForecast: 40 + i * 5,
      socActual: null,
      importRate: 6.67 + i,
      exportRate: 15.0049,
      cost: 0.1,
    })),
  };
  const now = new Date().toISOString();
  const text =
    "Predbat sat in `FrzExp` from 2026-10-05T01:00:00Z, then HoldChrg; ChatGpt checked the telemetry and sensor.predbat_status said Chrg 70%. `load_scaling` stayed 1.08 (brief sent 2026-10-05 04:16 UTC).";
  const base = payload.state.investigations[0];
  payload.state.investigations = [
    {
      ...base,
      id: "lint-investigation",
      at: now,
      status: "Completed",
      verdict: "problem",
      provider: "ChatGpt",
      dismissedAt: null,
      title: "Battery sat in FrzExp while `load_scaling` was 1.08",
      summary: text,
      evidence: [text],
      steps: ["Read telemetry_intervals"],
      nextSteps: [
        {
          id: "lint-open",
          title: "Check the FrzChg window",
          rationale: text,
          suggestedAction: "Look at `select.predbat_mode` during HoldChrg.",
          verification: text,
          uncertainty: "None",
          evidenceReferences: [],
          status: "open",
        },
        {
          id: "lint-closed",
          title: "Old HoldChrg question",
          rationale: text,
          suggestedAction: text,
          verification: text,
          uncertainty: "None",
          evidenceReferences: [],
          status: "closed",
          closedAt: "2026-10-04T12:50:27Z",
          closedReason: "Closed at 2026-10-04 12:50:27Z: superseded by a newer check (FrzExp resolved).",
        },
      ],
      fileChanges: [],
    },
    ...payload.state.investigations.slice(1).map((i: { id: string }) => ({ ...i })),
  ];
  const proposal = payload.state.proposals[0];
  if (proposal)
    payload.state.proposals = [
      {
        ...proposal,
        id: "lint-proposal",
        status: "Pending",
        title: "Raise the target above Chrg 70%",
        summary: text,
        expectedEffect: text,
        tradeoff: text,
        evidence: [text],
      },
      ...payload.state.proposals.slice(1),
    ];
  payload.state.analysisError = null;
  // An unread report whose title came straight from the server, for the notifications bell.
  payload.state.notifications = [
    {
      id: "lint-notification",
      at: now,
      title: "Missing export_today caused a warning every 5 minutes (FrzChg at 2026-10-05T01:00:00Z)",
      message: text,
      reportId: "lint-report",
      readAt: null,
    },
    ...(payload.state.notifications ?? []),
  ];
  // The bell reads the server's inbox: the same raw titles, as the server would store them.
  const inboxItem = (id: string, event: string, label: string, title: string, link: string) => ({
    id,
    key: `${event}:${id}`,
    event,
    label,
    title,
    detail: text,
    link,
    tone: "accent",
    at: now,
    readAt: null,
    dismissedAt: null,
    resolvedAt: null,
    open: true,
  });
  payload.state.inbox = [
    inboxItem(
      "lint-finding",
      "problem",
      "Found something",
      "Battery sat in FrzExp while `load_scaling` was 1.08",
      "#/insights/inv/lint-investigation",
    ),
    inboxItem(
      "lint-report-item",
      "report",
      "Report",
      "Missing export_today caused a warning every 5 minutes (FrzChg at 2026-10-05T01:00:00Z)",
      "#/energy/reports?report=lint-report",
    ),
  ];
  // A saved report with the same raw title and AI-written text: the Energy page must show both in plain English.
  const midnight = new Date(Date.now() - 86400000);
  midnight.setUTCHours(23, 0, 0, 0);
  const reportFrom = new Date(midnight.getTime() - 86400000).toISOString(),
    reportTo = midnight.toISOString();
  payload.state.reports = [
    {
      id: "lint-report",
      kind: "Daily",
      timeZone: "Europe/London",
      from: reportFrom,
      to: reportTo,
      createdAt: now,
      title: "Missing export_today caused a warning (FrzChg at 2026-10-05T01:00:00Z)",
      summary: text,
      isDemo: false,
      readAt: null,
      energySummary: null,
      days: [],
      investigationIds: ["lint-investigation"],
      textVersion: 2,
    },
    ...(payload.state.reports ?? []),
  ];
  await page.route("**/api/reports/lint-report", (route) =>
    route.fulfill({
      json: {
        ...payload.state.reports[0],
        partial: false,
        hasReadings: false,
        energySummary: null,
        relatedChecks: [{ id: "lint-investigation", at: now, title: "Battery sat in FrzExp while `load_scaling` was 1.08" }],
      },
    }),
  );
  await page.route("**/api/state", (route) => route.fulfill({ json: payload }));
  await page.route("**/api/investigations/lint-investigation", (route) =>
    route.fulfill({ json: payload.state.investigations[0] }),
  );
}

test("live-shaped AI text, plan slots and closure reasons render in plain English", async ({ page }) => {
  await liveShaped(page);
  // The bell lists the live-shaped finding and report: their raw titles must come out in plain English.
  await page.goto("/#/today");
  await page
    .getByRole("button", { name: /^Notifications/ })
    .first()
    .click();
  const bell = page.getByRole("dialog", { name: "Notifications" });
  await expect(bell).toContainText("Battery sat in");
  await expect(bell.locator("code", { hasText: "export_today" }).first()).toBeVisible();
  await page.keyboard.press("Escape");
  expect(await lint(page, "live-shaped")).toEqual([]);
});
