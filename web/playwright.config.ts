import { defineConfig, devices } from "@playwright/test";
const baseURL = process.env.E2E_BASE_URL || "http://127.0.0.1:5173";
const testPort = Number(new URL(baseURL).port || 5173);
export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  workers: 1,
  use: {
    baseURL,
    trace: "retain-on-failure",
  },
  projects: [
    {
      name: "chromium",
      use: {
        ...devices["Desktop Chrome"],
        channel: process.env.PLAYWRIGHT_CHANNEL,
      },
    },
  ],
  webServer: {
    command: `npm run dev -- --port ${testPort} --strictPort`,
    url: baseURL,
    reuseExistingServer: process.env.E2E_MANAGED !== "1",
    timeout: 30000,
  },
});
