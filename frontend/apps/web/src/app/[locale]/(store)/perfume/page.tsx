import type { ProductCard as Card, Taxonomy } from "@rahiq/api-client";
import { type Locale, translator } from "@rahiq/i18n";
import type { Metadata } from "next";
import Link from "next/link";
import { ProductCard } from "@/components/product/ProductCard";
import { BottleHero } from "@/components/world/BottleHero";
import { CausticsLayer } from "@/components/world/CausticsLayer";
import { FilterBar } from "@/components/world/FilterBar";
import { NotesJourney } from "@/components/world/NotesJourney";
import { apiGet, apiGetOrNull } from "@/lib/server/api";

type Search = { family?: string; gender?: string; concentration?: string; season?: string; sort?: string };

export async function generateMetadata({ params }: { params: Promise<{ locale: string }> }): Promise<Metadata> {
  const locale = (await params).locale as Locale;
  const t = translator(locale);
  return { title: t("perfume.title"), description: t("perfume.intro"), alternates: { canonical: `/${locale}/perfume` } };
}

export default async function PerfumeWorld({ params, searchParams }: { params: Promise<{ locale: string }>; searchParams: Promise<Search> }) {
  const locale = (await params).locale as Locale;
  const search = await searchParams;
  const t = translator(locale);
  const query = new URLSearchParams({ section: "perfume", ...Object.fromEntries(Object.entries(search).filter(([, v]) => typeof v === "string")) });
  const [products, taxonomy, featuredList] = await Promise.all([
    apiGet<Card[]>(`/api/catalog/products?${query}`, { locale }),
    apiGet<Taxonomy>("/api/catalog/taxonomy", { locale, revalidate: 3600 }),
    apiGet<Card[]>("/api/catalog/products?section=perfume&featured=true&limit=1", { locale }),
  ]);

  const featured = featuredList[0];
  const detail = featured ? await apiGetOrNull<{ attributes: { notes?: Record<"top" | "heart" | "base", string[]> } }>(`/api/catalog/products/${featured.slug}`, { locale, tags: ["catalog", `product:${featured.slug}`] }) : null;
  const noteName = (id: string) => taxonomy.notes.find((n) => n.code === id)?.name ?? id;
  const notes = detail?.attributes.notes;

  return (
    <div data-world="perfume">
      <section className="world-hero world-hero-perfume">
        <CausticsLayer world="perfume" />
        <div className="container world-hero-inner">
          <div>
            <p className="eyebrow">{t("home.doorPerfumeTitle")}</p>
            <h1>{t("perfume.title")}</h1>
            <p className="lede">{t("perfume.intro")}</p>
            <Link className="btn" href={`/${locale}/guide`}>
              {t("perfume.guideCta")}
            </Link>
          </div>
          <BottleHero />
        </div>
      </section>

      {featured && notes && (
        <NotesJourney
          title={t("perfume.journeyTitle")}
          name={featured.name}
          tiers={[
            { key: "top", title: t("perfume.top"), hint: t("perfume.topHint"), notes: (notes.top ?? []).map(noteName) },
            { key: "heart", title: t("perfume.heart"), hint: t("perfume.heartHint"), notes: (notes.heart ?? []).map(noteName) },
            { key: "base", title: t("perfume.base"), hint: t("perfume.baseHint"), notes: (notes.base ?? []).map(noteName) },
          ]}
        />
      )}

      <section className="section" aria-labelledby="perfume-grid">
        <div className="container">
          <h2 id="perfume-grid" className="sr-only">
            {t("perfume.title")}
          </h2>
          <FilterBar
            base={`/${locale}/perfume`}
            current={search}
            clearLabel={t("filters.clear")}
            anyLabel={t("filters.any")}
            groups={[
              { key: "family", label: t("filters.family"), options: taxonomy.families },
              { key: "gender", label: t("filters.gender"), options: taxonomy.genders },
              { key: "concentration", label: t("filters.concentration"), options: taxonomy.concentrations },
              { key: "season", label: t("filters.season"), options: taxonomy.seasons },
            ]}
          />
          <p className="muted results-count" aria-live="polite">
            {t("filters.results", { count: products.length })}
          </p>
          {products.length === 0 ? (
            <p>{t("common.noResults")}</p>
          ) : (
            <div className="grid-products">
              {products.map((p, i) => (
                <ProductCard key={p.id} product={p} locale={locale} priority={i < 2} />
              ))}
            </div>
          )}
        </div>
      </section>
    </div>
  );
}
