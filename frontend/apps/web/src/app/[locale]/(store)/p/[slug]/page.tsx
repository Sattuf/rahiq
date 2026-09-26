import type { ProductDetail, Taxonomy } from "@rahiq/api-client";
import { type Locale, translator } from "@rahiq/i18n";
import { formatDate } from "@rahiq/ui";
import type { Metadata } from "next";
import Link from "next/link";
import { notFound, permanentRedirect } from "next/navigation";
import { BuyBox } from "@/components/product/BuyBox";
import { AllergenBox, ScaleBar, Warnings } from "@/components/product/Facts";
import { ProductVisual } from "@/components/product/ProductVisual";
import { NotesJourney } from "@/components/world/NotesJourney";
import { apiGet, apiGetOrNull, MovedError } from "@/lib/server/api";
import { config } from "@/lib/server/config";

type Props = { params: Promise<{ locale: string; slug: string }> };

async function load(locale: Locale, slug: string) {
  try {
    return await apiGetOrNull<ProductDetail>(`/api/catalog/products/${encodeURIComponent(slug)}`, { locale, tags: ["catalog", `product:${slug}`] });
  } catch (error) {
    if (error instanceof MovedError) permanentRedirect(`/${locale}/p/${error.slug}`); // Old slugs keep working (content-seo.md §3).
    throw error;
  }
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { locale, slug } = await params;
  const product = await load(locale as Locale, slug);
  if (!product) return {};
  return {
    title: product.seoTitle ?? product.card.name,
    description: product.seoDescription ?? product.card.shortDescription ?? undefined,
    alternates: { canonical: `/${locale}/p/${slug}`, languages: Object.fromEntries(product.locales.map((l) => [l, `/${l}/p/${slug}`])) },
    openGraph: { title: product.card.name, description: product.card.shortDescription ?? undefined, ...(product.media[0] ? { images: [product.media[0].url] } : {}) },
  };
}

export default async function ProductPage({ params }: Props) {
  const { locale: raw, slug } = await params;
  const locale = raw as Locale;
  const t = translator(locale);
  const [product, taxonomy] = await Promise.all([load(locale, slug), apiGet<Taxonomy>("/api/catalog/taxonomy", { locale, revalidate: 3600 })]);
  if (!product) notFound(); // A sold-out product keeps its page with "not available now"; only a missing one is 404.

  const card = product.card;
  const a = product.attributes as Record<string, unknown>;
  const isPerfume = card.section === "perfume";
  const isHoney = card.section === "honey";
  const world = isPerfume ? "perfume" : isHoney ? "honey" : undefined;
  const name = (list: { code: string; name: string }[], code?: unknown) => list.find((x) => x.code === code)?.name ?? (code as string | undefined);
  const notes = a.notes as Record<"top" | "heart" | "base", string[]> | undefined;
  const taste = a.taste as { sweetness?: number; bitterness?: number; intensity?: number } | undefined;
  const composition = a.composition as { ingredient: Record<string, string>; percent: number }[] | undefined;
  const batch = product.batches.find((b) => product.variants.some((v) => v.id === b.variantId && !v.isSample)) ?? product.batches[0];
  const sectionHref = isPerfume ? `/${locale}/perfume` : isHoney ? `/${locale}/honey` : `/${locale}/gift-box`;
  const sectionName = isPerfume ? t("nav.perfume") : isHoney ? t("nav.honey") : t("nav.gifts");

  const jsonLd = [
    {
      "@context": "https://schema.org",
      "@type": "Product",
      name: card.name,
      description: card.shortDescription ?? undefined,
      sku: product.variants[0]?.sku,
      brand: { "@type": "Brand", name: "Rahiq" },
      image: product.media.map((m) => m.url),
      offers: product.variants.filter((v) => v.price != null).map((v) => ({
        "@type": "Offer",
        sku: v.sku,
        gtin: v.gtin ?? undefined,
        price: (v.price! / 100).toFixed(2),
        priceCurrency: card.currency,
        availability: v.availability === "out" ? "https://schema.org/OutOfStock" : "https://schema.org/InStock",
        url: `${config.siteUrl}/${locale}/p/${slug}`,
      })),
    },
    {
      "@context": "https://schema.org",
      "@type": "BreadcrumbList",
      itemListElement: [
        { "@type": "ListItem", position: 1, name: t("nav.home"), item: `${config.siteUrl}/${locale}` },
        { "@type": "ListItem", position: 2, name: sectionName, item: `${config.siteUrl}${sectionHref}` },
        { "@type": "ListItem", position: 3, name: card.name },
      ],
    },
  ];

  return (
    <div data-world={world}>
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: JSON.stringify(jsonLd).replace(/</g, "\\u003c") }} />
      <div className="container product-page">
        <nav aria-label={t("product.breadcrumbs")} className="breadcrumbs muted small">
          <Link href={`/${locale}`}>{t("nav.home")}</Link> / <Link href={sectionHref}>{sectionName}</Link> / <span aria-current="page">{card.name}</span>
        </nav>

        <div className="product-grid">
          <div className="gallery">
            {product.media.length > 0 ? (
              product.media.map((m, i) => <ProductVisual key={m.id} media={m} section={card.section} name={card.name} note="" priority={i === 0} sizes="(max-width: 900px) 100vw, 55vw" />)
            ) : (
              <ProductVisual section={card.section} name={card.name} note={t("common.photoSoon")} priority sizes="(max-width: 900px) 100vw, 55vw" />
            )}
          </div>

          <div className="product-info">
            <p className="eyebrow">{name(taxonomy.productTypes, card.type)}</p>
            <h1 className="product-title">{card.name}</h1>
            {card.shortDescription && <p className="lede">{card.shortDescription}</p>}

            {product.variants.length > 0 ? (
              <BuyBox variants={product.variants} currency={card.currency} back={`/${locale}/p/${slug}`} isPerfume={isPerfume} />
            ) : null}

            {card.type === "gift_box" && (
              <Link className="btn" href={`/${locale}/gift-box`}>
                {t("product.buildBox")}
              </Link>
            )}

            <Warnings warnings={product.warnings} locale={locale} />
            <AllergenBox allergens={product.allergens} locale={locale} />

            {isPerfume && (
              <section className="facts">
                <dl className="fact-list">
                  {a.concentration ? (
                    <div>
                      <dt>{t("filters.concentration")}</dt>
                      <dd>{name(taxonomy.concentrations, a.concentration)}</dd>
                    </div>
                  ) : null}
                  {Array.isArray(a.families) && a.families.length > 0 ? (
                    <div>
                      <dt>{t("filters.family")}</dt>
                      <dd>{(a.families as string[]).map((f) => name(taxonomy.families, f)).join(" · ")}</dd>
                    </div>
                  ) : null}
                  {a.gender ? (
                    <div>
                      <dt>{t("filters.gender")}</dt>
                      <dd>{name(taxonomy.genders, a.gender)}</dd>
                    </div>
                  ) : null}
                </dl>
                <ScaleBar label={t("product.intensity")} value={a.intensity as number} locale={locale} />
                <ScaleBar label={t("product.longevity")} value={a.longevity as number} locale={locale} />
                <ScaleBar label={t("product.sillage")} value={a.sillage as number} locale={locale} />
              </section>
            )}

            {isHoney && (
              <section className="facts" aria-labelledby="taste-title">
                {taste && (
                  <>
                    <h2 id="taste-title" className="h-small">
                      {t("product.taste")}
                    </h2>
                    <ScaleBar label={t("product.sweetness")} value={taste.sweetness} locale={locale} />
                    <ScaleBar label={t("product.bitterness")} value={taste.bitterness} locale={locale} />
                    <ScaleBar label={t("product.strength")} value={taste.intensity} locale={locale} />
                  </>
                )}
                <dl className="fact-list">
                  {a.floralSource || a.honeyType ? (
                    <div>
                      <dt>{t("filters.floralSource")}</dt>
                      <dd>{name(taxonomy.floralSources, a.floralSource ?? a.honeyType)}</dd>
                    </div>
                  ) : null}
                  {a.texture ? (
                    <div>
                      <dt>{t("product.texture")}</dt>
                      <dd>{name(taxonomy.textures, a.texture)}</dd>
                    </div>
                  ) : null}
                  {a.region ? (
                    <div>
                      <dt>{t("product.origin")}</dt>
                      <dd>{(a.region as Record<string, string>)[locale] ?? (a.region as Record<string, string>).tr}</dd>
                    </div>
                  ) : null}
                </dl>
                {product.crystallizationNote && (
                  <p className="notice small">
                    <strong>{t("product.crystallization")}: </strong>
                    {product.crystallizationNote}
                  </p>
                )}
              </section>
            )}

            {composition && composition.length > 0 && (
              <section className="facts" aria-labelledby="composition-title">
                <h2 id="composition-title" className="h-small">
                  {t("product.composition")}
                </h2>
                <div className="composition-bar" aria-hidden="true">
                  {composition.map((c, i) => (
                    <span key={i} style={{ inlineSize: `${c.percent}%` }} className={`comp comp-${i}`} />
                  ))}
                </div>
                <ul className="composition-list">
                  {composition.map((c, i) => (
                    <li key={i}>
                      <span className={`swatch comp-${i}`} aria-hidden="true" />
                      {c.ingredient[locale] ?? c.ingredient.tr} · %{c.percent}
                    </li>
                  ))}
                </ul>
              </section>
            )}

            {batch && isHoney && (
              <section className="batch-card" aria-labelledby="batch-title">
                <h2 id="batch-title" className="h-small">
                  {t("product.batchTitle")}
                </h2>
                <dl className="fact-list">
                  <div>
                    <dt>{t("product.batchCode")}</dt>
                    <dd>{batch.code}</dd>
                  </div>
                  {batch.origin?.season ? (
                    <div>
                      <dt>{t("product.harvest")}</dt>
                      <dd>{String(batch.origin.season)}</dd>
                    </div>
                  ) : null}
                  {batch.bestBefore && (
                    <div>
                      <dt>{t("product.bestBefore")}</dt>
                      <dd>{formatDate(batch.bestBefore, locale)}</dd>
                    </div>
                  )}
                </dl>
                <p className={batch.analysed ? "badge-analysed" : "muted small"}>{batch.analysed ? t("product.analysed") : t("product.notAnalysed")}</p>
                <Link href={`/${locale}/batch/${batch.token}`}>{t("product.batchPage")} →</Link>
              </section>
            )}

            {product.bundleParts.length > 0 && (
              <section className="facts">
                <h2 className="h-small">{t("product.bundleContains")}</h2>
                <ul>
                  {product.bundleParts.map((p) => (
                    <li key={p.variantId}>
                      {p.qty} × {p.name} {p.label}
                    </li>
                  ))}
                </ul>
              </section>
            )}

            {product.story && (
              <section className="facts">
                <h2 className="h-small">{t("product.story")}</h2>
                <p>{product.story}</p>
              </section>
            )}
            {product.usage && (
              <section className="facts">
                <h2 className="h-small">{t("product.usage")}</h2>
                <p>{product.usage}</p>
              </section>
            )}

            {isPerfume && Array.isArray(a.inci) && (
              <details className="accordion">
                <summary>{t("product.inci")}</summary>
                <p className="inci">{(a.inci as string[]).join(", ")}</p>
                {Array.isArray(a.declaredAllergens) && (a.declaredAllergens as string[]).length > 0 && (
                  <p>
                    <strong>{t("product.declaredAllergens")}:</strong> {(a.declaredAllergens as string[]).join(", ")}
                  </p>
                )}
              </details>
            )}

            <p className="muted small">{t("product.returnNote")}</p>
          </div>
        </div>
      </div>

      {isPerfume && notes && (
        <NotesJourney
          compact
          title={t("product.notes")}
          name={card.name}
          tiers={(["top", "heart", "base"] as const).filter((k) => (notes[k] ?? []).length > 0).map((k) => ({
            key: k,
            title: t(`perfume.${k}`),
            hint: t(`perfume.${k}Hint`),
            notes: (notes[k] ?? []).map((id) => name(taxonomy.notes, id) ?? id),
          }))}
        />
      )}
    </div>
  );
}
