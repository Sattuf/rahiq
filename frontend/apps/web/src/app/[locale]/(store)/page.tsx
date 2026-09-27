import type { ProductCard as Card } from "@rahiq/api-client";
import { type Locale, translator } from "@rahiq/i18n";
import Link from "next/link";
import { ProductCard } from "@/components/product/ProductCard";
import { Petals, RoseBloom } from "@/components/world/RoseBloom";
import { StoryScroll } from "@/components/world/StoryScroll";
import { TwoDoors } from "@/components/world/TwoDoors";
import { apiGet } from "@/lib/server/api";
import { config } from "@/lib/server/config";

export default async function Home({ params }: { params: Promise<{ locale: string }> }) {
  const locale = (await params).locale as Locale;
  const t = translator(locale);
  const [perfumes, honeys] = await Promise.all([
    apiGet<Card[]>("/api/catalog/products?section=perfume&limit=4", { locale }).catch(() => []),
    apiGet<Card[]>("/api/catalog/products?section=honey&limit=4", { locale }).catch(() => []),
  ]);

  const organization = {
    "@context": "https://schema.org",
    "@type": "Organization",
    name: "Rahiq",
    url: config.siteUrl,
    logo: `${config.siteUrl}/icon.svg`,
  };

  return (
    <>
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: JSON.stringify(organization) }} />
      <TwoDoors
        tagline={t("home.tagline")}
        lede={t("home.lede")}
        perfume={{ title: t("home.doorPerfumeTitle"), text: t("home.doorPerfumeText"), cta: t("home.doorPerfumeCta"), href: `/${locale}/perfume` }}
        honey={{ title: t("home.doorHoneyTitle"), text: t("home.doorHoneyText"), cta: t("home.doorHoneyCta"), href: `/${locale}/honey` }}
        rose={<RoseBloom variant="hero" />}
        ambient={<Petals />}
      />

      <section className="section gift-band">
        <div className="container gift-band-inner">
          <div>
            <p className="eyebrow">{t("nav.gifts")}</p>
            <h2>{t("home.giftTitle")}</h2>
            <p className="lede">{t("home.giftText")}</p>
            <Link className="btn" href={`/${locale}/gift-box`}>
              {t("home.giftCta")}
            </Link>
          </div>
          <div className="gift-band-art" aria-hidden="true">
            <span className="ribbon ribbon-perfume" />
            <span className="ribbon ribbon-honey" />
            <RoseBloom variant="still" className="gift-rose" />
          </div>
        </div>
      </section>

      {[
        { title: t("home.bestPerfume"), items: perfumes, href: `/${locale}/perfume`, world: "perfume" },
        { title: t("home.bestHoney"), items: honeys, href: `/${locale}/honey`, world: "honey" },
      ].map((block) =>
        block.items.length === 0 ? null : (
          <section key={block.href} className="section picks" data-world={block.world}>
            <div className="container">
              <div className="section-head">
                <h2>{block.title}</h2>
                <Link href={block.href}>{t("common.viewAll")} →</Link>
              </div>
              <div className="grid-products">
                {block.items.map((p) => (
                  <ProductCard key={p.id} product={p} locale={locale} />
                ))}
              </div>
            </div>
          </section>
        ),
      )}

      <StoryScroll title={t("home.storyTitle")} lines={[t("home.story1"), t("home.story2"), t("home.story3")]} visual={<RoseBloom variant="scroll" />}>
        <div className="container rose-trio">
          <p className="eyebrow rose-trio-eyebrow">{t("home.roseEyebrow")}</p>
          <h3 className="rose-trio-title">{t("home.roseTitle")}</h3>
          <ul className="rose-trio-list">
            <li data-essence="perfume">
              <Link href={`/${locale}/perfume`} transitionTypes={["world-perfume"]}>
                <span className="essence-icon" aria-hidden="true" />
                <strong>{t("home.rosePerfumeTitle")}</strong>
                <span>{t("home.rosePerfumeText")}</span>
              </Link>
            </li>
            <li data-essence="nectar">
              <span className="essence-icon" aria-hidden="true" />
              <strong>{t("home.roseNectarTitle")}</strong>
              <span>{t("home.roseNectarText")}</span>
            </li>
            <li data-essence="honey">
              <Link href={`/${locale}/honey`} transitionTypes={["world-honey"]}>
                <span className="essence-icon" aria-hidden="true" />
                <strong>{t("home.roseHoneyTitle")}</strong>
                <span>{t("home.roseHoneyText")}</span>
              </Link>
            </li>
          </ul>
        </div>
      </StoryScroll>

      <section className="section trust">
        <div className="container trust-grid">
          {[
            [t("home.trustLabTitle"), t("home.trustLabText")],
            [t("home.trustPayTitle"), t("home.trustPayText")],
            [t("home.trustShipTitle"), t("home.trustShipText")],
          ].map(([title, text]) => (
            <div key={title}>
              <h3>{title}</h3>
              <p className="muted">{text}</p>
            </div>
          ))}
        </div>
      </section>
    </>
  );
}
