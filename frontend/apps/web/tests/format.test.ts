import { describe, expect, it } from "vitest";
import { formatDate, formatMoney } from "@rahiq/ui";

const strip = (s: string) => s.replace(/[‎‏  ]/g, " ").trim();

describe("formatMoney", () => {
  it("shows kuruş only when there are any", () => {
    expect(strip(formatMoney(42000, "TRY", "tr"))).toBe("₺420");
    expect(strip(formatMoney(42050, "TRY", "tr"))).toBe("₺420,50");
  });

  it("uses the lira sign and Latin digits in Arabic", () => {
    const text = strip(formatMoney(135000, "TRY", "ar"));
    expect(text).toContain("₺");
    expect(text).toMatch(/1[,.]?350/);
    expect(text).not.toMatch(/[٠-٩]/);
  });

  it("groups thousands the Turkish way", () => {
    expect(strip(formatMoney(12345600, "TRY", "tr"))).toBe("₺123.456");
  });
});

describe("formatDate", () => {
  it("writes the month in the reader's language with Latin digits", () => {
    const date = "2028-09-25T12:00:00Z";
    expect(formatDate(date, "tr")).toBe("25 Eylül 2028");
    expect(formatDate(date, "en")).toBe("25 September 2028");
    expect(formatDate(date, "ar")).toMatch(/^25 سبتمبر 2028$/);
  });
});
