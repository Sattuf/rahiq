import { describe, expect, it } from "vitest";
import { dirOf, isLocale, locales, messages, translate } from "@rahiq/i18n";

function keys(node: unknown, prefix = ""): string[] {
  return Object.entries(node as Record<string, unknown>).flatMap(([k, v]) =>
    typeof v === "string" ? [`${prefix}${k}`] : keys(v, `${prefix}${k}.`));
}

describe("dictionaries", () => {
  it("every language has exactly the Turkish keys", () => {
    const reference = keys(messages("tr")).sort();
    for (const locale of locales) expect(keys(messages(locale)).sort(), locale).toEqual(reference);
  });

  it("no value is empty", () => {
    for (const locale of locales) {
      const empty = keys(messages(locale)).filter((k) => translate(locale, k, { count: 1 }).trim() === "");
      expect(empty, locale).toEqual([]);
    }
  });

  it("placeholders survive translation", () => {
    const names = (s: string) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();
    for (const key of keys(messages("tr")).filter((k) => !/\.(zero|one|two|few|many|other)$/.test(k))) {
      const expected = names(translate("tr", key));
      for (const locale of ["ar", "en"] as const) expect(names(translate(locale, key)), `${locale}:${key}`).toEqual(expected);
    }
  });
});

describe("plurals", () => {
  it("Arabic uses all six forms", () => {
    const say = (count: number) => translate("ar", "common.items", { count });
    expect(say(0)).toBe("لا منتجات");
    expect(say(1)).toBe("منتج واحد");
    expect(say(2)).toBe("منتجان");
    expect(say(3)).toBe("3 منتجات");
    expect(say(11)).toBe("11 منتجًا");
    expect(say(100)).toBe("100 منتج");
  });

  it("Turkish and English put the number in", () => {
    expect(translate("tr", "common.items", { count: 3 })).toBe("3 ürün");
    expect(translate("en", "common.items", { count: 1 })).toMatch(/^1 /);
  });

  it("an unknown key falls back to the key, not to an empty string", () => {
    expect(translate("tr", "does.not.exist")).toBe("does.not.exist");
  });
});

describe("locales", () => {
  it("only Arabic is right-to-left", () => {
    expect(locales.map(dirOf)).toEqual(["ltr", "rtl", "ltr"]);
  });

  it("rejects anything but the three", () => {
    expect(isLocale("tr")).toBe(true);
    expect(isLocale("de")).toBe(false);
    expect(isLocale(undefined)).toBe(false);
  });
});
