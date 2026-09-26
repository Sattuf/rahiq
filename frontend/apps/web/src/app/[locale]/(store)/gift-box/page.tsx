import type { ProductCard, ProductDetail } from "@rahiq/api-client";
import { type Locale, translator } from "@rahiq/i18n";
import type { Metadata } from "next";
import { GiftBoxBuilder } from "@/components/world/GiftBoxBuilder";
import { apiGet, apiGetOrNull } from "@/lib/server/api";

export async function generateMetadata({ params }: { params: Promise<{ locale: string }> }): Promise<Metadata> {
  const locale = (await params).locale as Locale;
  const t = translator(locale);
  return { title: t("gift.title"), description: t("gift.intro") };
}

export default async function GiftBoxPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = (await params).locale as Locale;
  const t = translator(locale);
  const boxes = await apiGet<ProductCard[]>("/api/catalog/products?type=gift_box&limit=1", { locale });
  const box = boxes[0] ? await apiGetOrNull<ProductDetail>(`/api/catalog/products/${boxes[0].slug}`, { locale, tags: ["catalog", `product:${boxes[0].slug}`] }) : null;
  const variant = box?.variants[0];

  return (
    <div className="section">
      <div className="container">
        <p className="eyebrow">{t("nav.gifts")}</p>
        <h1>{t("gift.title")}</h1>
        <p className="lede">{t("gift.intro")}</p>
        {box && variant?.price != null ? (
          <GiftBoxBuilder boxVariantId={variant.id} boxPrice={variant.price} currency={box.card.currency} slots={box.giftSlots} />
        ) : (
          <p className="notice">{t("common.soldOut")}</p>
        )}
      </div>
    </div>
  );
}
