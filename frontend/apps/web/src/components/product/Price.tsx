import { translate, type Locale } from "@rahiq/i18n";
import { formatMoney } from "@rahiq/ui";

/**
 * A price, and the struck-through "previous price" only when the API says so: it is the lowest price of the
 * last 30 days, computed from history, never typed in (compliance.md §3).
 */
export function Price({ amount, previous, currency, locale, from }: { amount: number; previous?: number | null; currency: string; locale: Locale; from?: boolean }) {
  const now = formatMoney(amount, currency, locale);
  return (
    <span className="price-block">
      <span className="price">{from ? translate(locale, "common.from", { price: now }) : now}</span>
      {previous ? (
        <span className="price-old" title={translate(locale, "product.previousPrice")}>
          <span className="sr-only">{translate(locale, "product.previousPrice")}: </span>
          {formatMoney(previous, currency, locale)}
        </span>
      ) : null}
    </span>
  );
}
