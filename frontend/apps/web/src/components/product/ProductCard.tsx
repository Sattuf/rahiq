import type { ProductCard as Card } from "@rahiq/api-client";
import { type Locale, translator } from "@rahiq/i18n";
import Link from "next/link";
import { Price } from "./Price";
import { ProductVisual } from "./ProductVisual";

export function ProductCard({ product, locale, priority }: { product: Card; locale: Locale; priority?: boolean }) {
  const t = translator(locale);
  return (
    <Link href={`/${locale}/p/${product.slug}`} className="card-link">
      <ProductVisual media={product.image} section={product.section} name={product.name} note={t("common.photoSoon")} priority={priority} morph={`product-${product.slug}`} />
      <div className="card-body">
        <h3 className="card-title">{product.name}</h3>
        {product.shortDescription && <p className="muted card-text">{product.shortDescription}</p>}
        <div className="card-meta">
          {product.price !== null && product.price !== undefined ? (
            <Price amount={product.price} previous={product.previousPrice} currency={product.currency} locale={locale} from={product.priceVaries} />
          ) : null}
          {!product.available && <span className="muted small">{t("common.soldOut")}</span>}
          {product.hasSample && <span className="tag">{t("common.sample")}</span>}
        </div>
        {product.sizes.length > 0 && <p className="muted small">{product.sizes.join(" · ")}</p>}
      </div>
    </Link>
  );
}
