import { expect, type Page, test } from "@playwright/test";

const api = process.env.E2E_API_URL ?? "http://localhost:5080";

/** Checkout needs a cart: put one honey in it through the no-JS cart form. */
async function fillCart(page: Page) {
  const product = await (await page.request.get(`${api}/api/catalog/products/kestane-bali?locale=tr`)).json();
  await page.request.post("/api/cart-form", { form: { variantId: product.variants[0].id, qty: "1", back: "/tr" }, headers: { origin: new URL(page.url() === "about:blank" ? "http://localhost:3000" : page.url()).origin } });
}

// testing.md §4: the visual self-check list, automated where a machine can judge.
const pages = ["", "/perfume", "/honey", "/p/oud-taif-rose", "/p/kestane-bali", "/p/balli-ceviz-findik", "/gift-box", "/checkout"];

for (const locale of ["tr", "ar", "en"]) {
  for (const path of pages) {
    test(`${locale}${path || "/"}: no horizontal scroll, no console errors, screenshot`, async ({ page }, info) => {
      const errors: string[] = [];
      page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
      page.on("pageerror", (e) => errors.push(e.message));
      if (path === "/checkout") await fillCart(page);
      await page.goto(`/${locale}${path}`);
      await page.waitForLoadState("networkidle");
      // Scroll-linked reveals only finish once the reader has passed them; walk the page like a reader would.
      await page.evaluate(async () => {
        for (let y = 0; y < document.body.scrollHeight; y += 400) {
          window.scrollTo(0, y);
          await new Promise((r) => setTimeout(r, 60));
        }
        window.scrollTo(0, 0);
      });

      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow, "horizontal scroll").toBeLessThanOrEqual(1);
      expect(errors.filter((e) => !/favicon|Failed to load resource.*404/.test(e))).toEqual([]);
      await page.screenshot({ path: `test-results/screens/${info.project.name}-${locale}${path.replaceAll("/", "_") || "_home"}.png`, fullPage: true });
    });
  }
}

test("warnings are real text on every product that needs them, in each language", async ({ page }) => {
  for (const [locale, text] of [["tr", "1 yaşından küçük"], ["ar", "لا يُعطى للأطفال"], ["en", "infants under 12 months"]] as const) {
    await page.goto(`/${locale}/p/corekotu-zencefil-bal`);
    await expect(page.getByText(new RegExp(text))).toBeVisible();
  }
});

test.describe("reduced motion", () => {
  test.use({ reducedMotion: "reduce" });
  test("the honey pour shows its final state and nothing animates", async ({ page }) => {
    await page.goto("/tr/honey");
    const pour = await page.locator(".pour").evaluate((el) => getComputedStyle(el).getPropertyValue("--pour").trim());
    expect(pour).toBe("1");
  });
});

test("checkout ships no 3D, shader or animation library (Law 10)", async ({ page }) => {
  const scripts: string[] = [];
  page.on("response", async (r) => {
    if (r.url().endsWith(".js")) scripts.push(await r.text());
  });
  await fillCart(page);
  await page.goto("/tr/checkout");
  await expect(page).toHaveURL(/\/tr\/checkout/);
  await page.waitForLoadState("networkidle");
  const joined = scripts.join("\n");
  for (const marker of ["WebGLRenderer", "MeshTransmissionMaterial", "ScrollTrigger", "lenis", "gl_FragColor"]) {
    expect(joined.includes(marker), marker).toBe(false);
  }
});
