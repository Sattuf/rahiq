"use client";

import type { OrderView } from "@rahiq/api-client";
import { formatDate, formatMoney } from "@rahiq/ui";
import { useLocale, useT } from "@/lib/client/i18n";

export function OrderDetail({ order, children }: { order: OrderView; children?: React.ReactNode }) {
  const t = useT();
  const locale = useLocale();
  const money = (v: number) => formatMoney(v, order.currency, locale);

  return (
    <article className="order-detail">
      <header className="order-head">
        <div>
          <p className="eyebrow">{t("order.number")}</p>
          <h2>{order.number}</h2>
          <p className="muted">
            {t("order.placed")}: {formatDate(order.placedAt, locale, true)}
          </p>
        </div>
        <p className={`status status-${order.status}`}>{t(`order.statuses.${order.status}`)}</p>
      </header>

      <ol className="timeline" aria-label={t("order.status")}>
        {order.history.map((h, i) => (
          <li key={`${h.status}-${i}`}>
            <strong>{t(`order.statuses.${h.status}`)}</strong> <span className="muted small">{formatDate(h.at, locale, true)}</span>
          </li>
        ))}
      </ol>

      {order.trackingNumber && (
        <p>
          {t("order.tracking")}: <strong>{order.trackingNumber}</strong>
        </p>
      )}

      <h3>{t("order.items")}</h3>
      <ul className="order-lines">
        {order.lines.map((l) => (
          <li key={l.id}>
            <div>
              <strong>{l.groupLabel ? `${l.groupLabel}: ` : ""}{l.name}</strong> <span className="muted">{l.variantLabel} × {l.qty}</span>
              {l.batches.length > 0 && (
                <span className="muted small">
                  {" "}
                  · {t("order.batches")} {l.batches.map((b) => b.code).join(", ")}
                </span>
              )}
              {l.warnings.length > 0 && <p className="small muted">{l.warnings.join(" ")}</p>}
            </div>
            {!order.hidePrices && <span className="price">{money(l.lineTotal)}</span>}
          </li>
        ))}
      </ul>

      {!order.hidePrices && (
        <dl className="totals">
          <div>
            <dt>{t("common.subtotal")}</dt>
            <dd>{money(order.subtotal)}</dd>
          </div>
          {order.discount > 0 && (
            <div>
              <dt>{t("common.discount")}</dt>
              <dd>−{money(order.discount)}</dd>
            </div>
          )}
          <div>
            <dt>{t("common.shipping")}</dt>
            <dd>{order.shipping === 0 ? t("common.free") : money(order.shipping)}</dd>
          </div>
          {order.codFee > 0 && (
            <div>
              <dt>{t("common.codFee")}</dt>
              <dd>{money(order.codFee)}</dd>
            </div>
          )}
          <div className="grand">
            <dt>{t("common.total")}</dt>
            <dd>{money(order.total)}</dd>
          </div>
          <div className="muted small">
            <dt>{t("common.tax")}</dt>
            <dd>{money(order.tax)}</dd>
          </div>
        </dl>
      )}

      <p className="muted small">
        {t("order.payment")}: {order.paymentMethod === "cod" ? t("checkout.cod") : `${t("checkout.card")} · ${t("order.installments", { count: order.installments })}`}
      </p>
      <address className="muted small">
        {order.shippingAddress.fullName}, {order.shippingAddress.line1} {order.shippingAddress.line2}, {order.shippingAddress.district} / {order.shippingAddress.provinceName}
      </address>
      {children}
    </article>
  );
}
