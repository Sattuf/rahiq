import { defineConfig } from "vitest/config";

// Unit tests for the pure parts (i18n, formatting, tokens). Journeys live in e2e/ and run under Playwright.
export default defineConfig({
  test: { include: ["tests/**/*.test.ts"], environment: "node" },
});
