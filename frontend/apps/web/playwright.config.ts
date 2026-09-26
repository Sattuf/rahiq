import { defineConfig, devices } from "@playwright/test";

/**
 * Journeys from testing.md §3 against a running stack (API seeded with `dotnet Rahiq.Api.dll seed`, web on :3000).
 * CI starts both; locally: run the API and `pnpm start`, then `pnpm test:e2e`.
 */
export default defineConfig({
  testDir: "./e2e",
  timeout: 90_000,
  fullyParallel: false,
  retries: process.env.CI ? 1 : 0,
  reporter: [["list"], ["html", { open: "never" }]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "http://localhost:3000",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [
    { name: "desktop", use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 } } },
    // testing.md §4.1: the self-check also runs at the laptop and the small-phone sizes.
    { name: "laptop", use: { ...devices["Desktop Chrome"], viewport: { width: 1280, height: 800 } }, testMatch: /self-check/ },
    { name: "phone", use: { ...devices["Pixel 7"], viewport: { width: 390, height: 844 } }, testMatch: /self-check/ },
    { name: "small-phone", use: { ...devices["Pixel 7"], viewport: { width: 375, height: 667 } }, testMatch: /self-check/ },
  ],
});
