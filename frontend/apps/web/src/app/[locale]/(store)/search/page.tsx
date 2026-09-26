import type { ProductCard as Card } from "@rahiq/api-client";
import { type Locale, translator } from "@rahiq/i18n";
import type { Metadata } from "next";
import { ProductCard } from "@/components/product/ProductCard";
import { apiGet } from "@/lib/server/api";

export const metadata: Metadata = { robots: { index: false } };

export default async function SearchPage({ params, searchParams }: { params: Promise<{ locale: string }>; searchParams: Promise<{ q?: string }> }) {
  const locale = (await params).locale as Locale;
  const { q = "" } = await searchParams;
  const t = translator(locale);
  const results = q.trim() ? await apiGet<Card[]>(`/api/catalog/search?q=${encodeURIComponent(q)}`, { locale, revalidate: 60 }) : [];

  return (
    <div className="container section">
      <h1>{t("nav.search")}</h1>
      <form role="search" className="search-form" action={`/${locale}/search`}>
        <label htmlFor="q" className="sr-only">
          {t("common.search")}
        </label>
        <input id="q" name="q" type="search" className="input" defaultValue={q} placeholder={t("common.searchPlaceholder")} autoComplete="off" />
        <button className="btn" type="submit">
          {t("common.search")}
        </button>
      </form>
      {q && (
        <p className="muted results-count" aria-live="polite">
          {t("filters.results", { count: results.length })}
        </p>
      )}
      <div className="grid-products">
        {results.map((p) => (
          <ProductCard key={p.id} product={p} locale={locale} />
        ))}
      </div>
    </div>
  );
}
