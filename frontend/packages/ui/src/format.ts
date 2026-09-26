const tags: Record<string, string> = { tr: "tr-TR", ar: "ar", en: "en-GB" };

/** Money arrives in minor units (kuruş) and is formatted, never computed, in the browser (Law 5). */
export function formatMoney(minor: number, currency: string, locale: string): string {
  return new Intl.NumberFormat(tags[locale] ?? "tr-TR", {
    style: "currency",
    currency,
    currencyDisplay: "narrowSymbol",
    minimumFractionDigits: minor % 100 === 0 ? 0 : 2,
    maximumFractionDigits: 2,
    numberingSystem: "latn",
  }).format(minor / 100);
}

export function formatDate(value: string | Date, locale: string, withTime = false): string {
  const date = typeof value === "string" ? new Date(value) : value;
  return new Intl.DateTimeFormat(tags[locale] ?? "tr-TR", {
    day: "numeric",
    month: "long",
    year: "numeric",
    ...(withTime ? { hour: "2-digit", minute: "2-digit" } : {}),
    numberingSystem: "latn",
  }).format(date);
}
