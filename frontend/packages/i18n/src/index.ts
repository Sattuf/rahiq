// Server-side i18n: all three dictionaries. Client components use "@rahiq/i18n/core" with the messages the server passes.
import { ar } from "./ar";
import { defaultLocale, type Locale, type MessageKey, type Messages, type T, translateWith } from "./core";
import { en } from "./en";
import { tr } from "./tr";

export * from "./core";

const dictionaries: Record<Locale, Messages> = { tr, ar, en };

export function messages(locale: Locale): Messages {
  return dictionaries[locale] ?? dictionaries[defaultLocale];
}

export function translate(locale: Locale, key: MessageKey | (string & {}), values?: Record<string, string | number>): string {
  const text = translateWith(messages(locale), locale, key, values);
  return text === key && locale !== defaultLocale ? translateWith(dictionaries[defaultLocale], defaultLocale, key, values) : text;
}

export function translator(locale: Locale): T {
  return (key, values) => translate(locale, key, values);
}
