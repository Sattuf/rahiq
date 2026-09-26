// The browser-safe part of i18n: no dictionaries here, so client bundles carry only the one language the server sends.
import type { tr } from "./tr";

export const locales = ["tr", "ar", "en"] as const;
export type Locale = (typeof locales)[number];
export const defaultLocale: Locale = "tr";

export const isLocale = (value: string | undefined | null): value is Locale => !!value && (locales as readonly string[]).includes(value);

export const dirOf = (locale: Locale) => (locale === "ar" ? "rtl" : "ltr");

/** BCP 47 tags for Intl (tr-TR keeps Turkish casing and digits correct). */
export const intlLocale: Record<Locale, string> = { tr: "tr-TR", ar: "ar", en: "en-GB" };

export type Messages = typeof tr;

type Path<T, P extends string = ""> = {
  [K in keyof T & string]: T[K] extends string ? `${P}${K}` : T[K] extends Record<string, unknown> ? Path<T[K], `${P}${K}.`> : never;
}[keyof T & string];

export type MessageKey = Path<Messages>;

function lookup(dict: unknown, key: string): string | Record<string, string> | undefined {
  return key.split(".").reduce<unknown>((node, part) => (node as Record<string, unknown> | undefined)?.[part], dict) as string | Record<string, string> | undefined;
}

/**
 * Translates a key with {placeholders}. A key whose value is an object of plural forms
 * (zero/one/two/few/many/other) is chosen with Intl.PluralRules, so Arabic gets all six forms.
 */
export function translateWith(dict: Messages, locale: Locale, key: MessageKey | (string & {}), values?: Record<string, string | number>): string {
  const found = lookup(dict, key);
  let text: string;
  if (typeof found === "string") {
    text = found;
  } else if (found && typeof found === "object") {
    const count = Number(values?.count ?? 0);
    const rule = count === 0 && found.zero ? "zero" : new Intl.PluralRules(intlLocale[locale]).select(count);
    text = found[rule] ?? found.other ?? key;
  } else {
    text = key;
  }

  return values ? text.replace(/\{(\w+)\}/g, (_, name: string) => String(values[name] ?? `{${name}}`)) : text;
}

export type T = (key: MessageKey | (string & {}), values?: Record<string, string | number>) => string;
