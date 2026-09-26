import { expect, type Page, test } from "@playwright/test";

const address = async (page: Page, email: string) => {
  await page.getByLabel(/E-posta|البريد الإلكتروني|E-mail/).first().fill(email);
  await page.getByLabel(/Ad soyad|الاسم الكامل|Full name/).fill("Ayşe Yılmaz");
  await page.getByLabel(/^Telefon$|^الهاتف$|^Phone$/).fill("+90 555 111 22 33");
  await page.getByLabel(/^İl$|المحافظة|Province/).selectOption({ label: "İstanbul" });
  await page.getByLabel(/^İlçe$|الحي \/ المنطقة|District/).fill("Kadıköy");
  await page.getByLabel(/^Adres$|^العنوان$|^Address$/).fill("Moda Cd. 12/3");
};

test.describe("testing.md §3 journeys", () => {
  test("1. perfume: world → product → sample + bottle → cart → card (sandbox) → confirmed", async ({ page }) => {
    await page.goto("/tr");
    await page.getByRole("link", { name: /Parfümlere gir/ }).click();
    await expect(page).toHaveURL(/\/tr\/perfume/);
    await page.getByRole("link", { name: /Oud & Taif Gülü/ }).first().click();
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("Oud & Taif Gülü");
    await expect(page.getByText("Yanıcıdır. Isı ve alevden uzak tutunuz.")).toBeVisible(); // Mandatory warning (Law 2).

    await page.getByRole("button", { name: /2 ml numune/ }).click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await page.getByRole("button", { name: /Kapat/ }).first().click();
    await page.getByRole("button", { name: /Sepete ekle/ }).click();
    await expect(page.getByRole("dialog").getByText("Oud & Taif Gülü").first()).toBeVisible();
    await page.getByRole("link", { name: "Ödemeye geç" }).click();

    await expect(page).toHaveURL(/\/tr\/checkout/);
    await address(page, `e2e-card-${Date.now()}@example.com`);
    await page.getByRole("button", { name: /Adresi kaydet/ }).click();
    await page.getByRole("button", { name: "Devam" }).click();
    await page.getByLabel(/Ön bilgilendirme formunu ve mesafeli/).check();
    await page.getByRole("button", { name: /öde$/ }).click();

    await expect(page).toHaveURL(/dev\/payments\/sandbox/);
    await page.getByTestId("sandbox-pay").click();
    await expect(page.getByRole("heading", { name: "Siparişiniz alındı" })).toBeVisible({ timeout: 30_000 });
  });

  test("2. honey: world → product → batch card → cart → cash on delivery", async ({ page }) => {
    await page.goto("/tr/honey");
    await page.getByRole("link", { name: /Kestane Balı/ }).first().click();
    await expect(page.getByText("Size gelecek parti")).toBeVisible();
    await expect(page.getByText("KST-2609A")).toBeVisible(); // FEFO: the batch expiring in 45 days is never shown or sold.
    await expect(page.getByText("1 yaşından küçük bebeklere verilmemelidir.")).toBeVisible();
    await page.getByRole("button", { name: /Sepete ekle/ }).click();
    await page.getByRole("link", { name: "Ödemeye geç" }).click();

    await address(page, `e2e-cod-${Date.now()}@example.com`);
    await page.getByRole("button", { name: /Adresi kaydet/ }).click();
    await page.getByRole("button", { name: "Devam" }).click();
    await page.getByLabel(/Kapıda ödeme/).check();
    await page.getByLabel(/Ön bilgilendirme formunu ve mesafeli/).check();
    await page.getByRole("button", { name: /Siparişi onayla/ }).click();
    await expect(page.getByRole("heading", { name: "Siparişiniz alındı" })).toBeVisible({ timeout: 30_000 });
  });

  test("3. gift box for another address with a message and hidden prices", async ({ page }) => {
    await page.goto("/tr/gift-box");
    await page.locator(".gift-step").nth(0).locator(".gift-option").first().click();
    await page.locator(".gift-step").nth(1).locator(".gift-option").first().click();
    await page.getByLabel(/Mesajınız/).fill("İyi ki doğdun!");
    await page.getByRole("button", { name: "Kutuyu sepete ekle" }).click();
    await expect(page.getByRole("dialog").getByText("“İyi ki doğdun!”")).toBeVisible();
    await page.getByRole("link", { name: "Ödemeye geç" }).click();

    await address(page, `e2e-gift-${Date.now()}@example.com`);
    await page.getByLabel("Başka birine hediye").check();
    await page.getByLabel("Pakette fiyat görünmesin").check();
    await page.getByRole("button", { name: /Adresi kaydet/ }).click();
    await expect(page.getByText(/İki Işık Hediye Kutusu/).first()).toBeVisible();
  });

  test("4. perfume guide to three suggestions", async ({ page }) => {
    await page.goto("/ar/guide");
    for (let i = 0; i < 4; i++) {
      await page.locator(".guide-option").first().click();
    }
    await expect(page.getByRole("heading", { name: "ثلاثة عطور نقترحها لك" })).toBeVisible();
  });

  test("5. the QR on the jar opens the batch page", async ({ page, request }) => {
    const product = await (await request.get(`${process.env.E2E_API_URL ?? "http://localhost:5080"}/api/catalog/products/kestane-bali?locale=ar`)).json();
    await page.goto(`/ar/batch/${product.batches[0].token}`);
    await expect(page.getByRole("heading", { level: 1 })).toContainText("KST-2609A");
    await expect(page.getByText(/الرطوبة 17.2%/)).toBeVisible();
  });

  test("7. returns: food is not returnable on withdrawal", async ({ page }) => {
    await page.goto("/tr/pages/iade");
    await expect(page.getByText(/cayma kapsamı dışındadır/)).toBeVisible();
  });
});
