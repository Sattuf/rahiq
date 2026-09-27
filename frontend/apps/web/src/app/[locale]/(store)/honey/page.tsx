import type { ProductCard as Card, Taxonomy } from "@rahiq/api-client";
import { type Locale, translator } from "@rahiq/i18n";
import type { Metadata } from "next";
import Link from "next/link";
import { ProductCard } from "@/components/product/ProductCard";
import { Atmosphere } from "@/components/world/Atmosphere";
import { CausticsLayer } from "@/components/world/CausticsLayer";
import { FilterBar } from "@/components/world/FilterBar";
import { HoneyPour } from "@/components/world/HoneyPour";
import { SourcesMap } from "@/components/world/SourcesMap";
import { apiGet } from "@/lib/server/api";

type Search = { type?: string; floralSource?: string; region?: string; texture?: string; sort?: string };

export async function generateMetadata({ params }: { params: Promise<{ locale: string }> }): Promise<Metadata> {
  const locale = (await params).locale as Locale;
  const t = translator(locale);
  return { title: t("honey.title"), description: t("honey.intro"), alternates: { canonical: `/${locale}/honey` } };
}

export default async function HoneyWorld({ params, searchParams }: { params: Promise<{ locale: string }>; searchParams: Promise<Search> }) {
  const locale = (await params).locale as Locale;
  const search = await searchParams;
  const t = translator(locale);
  const query = new URLSearchParams({ section: "honey" });
  if (search.type) query.set("type", search.type);
  if (search.floralSource) query.set("floralSource", search.floralSource);
  if (search.region) query.set("regionCode", search.region);
  if (search.texture) query.set("texture", search.texture);
  if (search.sort) query.set("sort", search.sort);

  const [products, all, taxonomy] = await Promise.all([
    apiGet<Card[]>(`/api/catalog/products?${query}`, { locale }),
    apiGet<Card[]>("/api/catalog/products?section=honey&limit=200", { locale }),
    apiGet<Taxonomy>("/api/catalog/taxonomy", { locale, revalidate: 3600 }),
  ]);

  const regions = taxonomy.regionCodes.map((code) => ({
    code,
    name: t(`honey.regions.${code}`),
    count: all.filter((p) => p.highlights.regionCode === code).length,
  }));
  const base = `/${locale}/honey`;
  const types = [
    { code: "honey", name: t("honey.raw") },
    { code: "comb_honey", name: t("honey.comb") },
    { code: "nuts_in_honey", name: t("honey.nuts") },
    { code: "honey_blend", name: t("honey.blends") },
  ];

  return (
    <div data-world="honey">
      <section className="world-hero world-hero-honey">
        <CausticsLayer world="honey" />
        <div className="container world-hero-inner">
          <div>
            <p className="eyebrow">{t("home.doorHoneyTitle")}</p>
            <h1>{t("honey.title")}</h1>
            <p className="lede">{t("honey.intro")}</p>
          </div>
          <div className="hero-vessel hero-vessel-honey">
            <Atmosphere name="honey-jar" sizes="(max-width: 900px) 90vw, 40vw" priority />
          </div>
        </div>
      </section>

      <HoneyPour caption={t("honey.pourCaption")} />

      <SourcesMap title={t("honey.mapTitle")} hint={t("honey.mapHint")} regions={regions} base={base} current={search.region} />

      <section className="section" aria-labelledby="honey-grid">
        <div className="container">
          <h2 id="honey-grid" className="sr-only">
            {t("honey.title")}
          </h2>
          <nav className="chips subsections" aria-label={t("honey.title")}>
            <Link className="chip" href={base} aria-current={!search.type ? "true" : undefined}>
              {t("honey.all")}
            </Link>
            {types.map((ty) => (
              <Link key={ty.code} className="chip" href={`${base}?type=${ty.code}`} aria-current={search.type === ty.code ? "true" : undefined}>
                {ty.name}
              </Link>
            ))}
          </nav>
          <FilterBar
            base={base}
            current={{ type: search.type, floralSource: search.floralSource, texture: search.texture, region: search.region }}
            clearLabel={t("filters.clear")}
            anyLabel={t("filters.any")}
            groups={[
              { key: "floralSource", label: t("filters.floralSource"), options: taxonomy.floralSources.filter((f) => all.some((p) => p.highlights.floralSource === f.code)) },
              { key: "texture", label: t("filters.texture"), options: taxonomy.textures },
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
