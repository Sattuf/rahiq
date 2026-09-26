import { expect, test } from "@playwright/test";

// testing.md §4 item 6: without WebGL the site is complete and beautiful (Law 11).
test.use({ launchOptions: { args: ["--disable-webgl", "--disable-3d-apis"] } });

test("the site is complete and purchasable without WebGL", async ({ page }) => {
  await page.goto("/tr/perfume");
  await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
  await expect(page.locator(".caustics-fallback").first()).toBeAttached();
  await page.goto("/tr/p/kestane-bali");
  await expect(page.getByRole("button", { name: /Sepete ekle/ })).toBeEnabled();
});
