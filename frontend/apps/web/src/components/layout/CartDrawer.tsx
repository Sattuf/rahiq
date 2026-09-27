"use client";

import * as Dialog from "@radix-ui/react-dialog";
import { ApiError } from "@rahiq/api-client";
import { formatMoney } from "@rahiq/ui";
import Link from "next/link";
import { useEffect, useState } from "react";
import { useCart, useCartMutations } from "@/lib/client/cart";
import { useErrorText, useLocale, useT } from "@/lib/client/i18n";
import { whatsappLink } from "@/lib/shared/whatsapp";
import { WhatsAppIcon } from "./WhatsAppButton";

/** With a WhatsApp number the order goes out as a written WhatsApp message; card checkout stays as the alternative. */
export function CartDrawer({ whatsapp }: { whatsapp?: string | null }) {
  const t = useT();
  const locale = useLocale();
  const errorText = useErrorText();
  const [open, setOpen] = useState(false);
  const { data: cart, isLoading } = useCart();
  const { update, remove, coupon } = useCartMutations();
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const show = () => setOpen(true);
    window.addEventListener("rahiq:open-cart", show);
    return () => window.removeEventListener("rahiq:open-cart", show);
  }, []);

  const money = (minor: number) => formatMoney(minor, cart?.currency ?? "TRY", locale);
  const fail = (e: unknown) => setError(errorText(e instanceof ApiError ? e.code : undefined));

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Portal>
        <Dialog.Overlay className="overlay" />
        <Dialog.Content className="drawer" aria-describedby={undefined}>
          <div className="drawer-head">
            <Dialog.Title className="display">{t("cart.title")}</Dialog.Title>
            <Dialog.Close className="btn btn-quiet">{t("nav.close")}</Dialog.Close>
          </div>

          <div className="drawer-body">
            {isLoading && <p className="muted">{t("common.loading")}</p>}
            {cart && cart.items.length === 0 && (
              <div className="empty">
                <p>{t("cart.empty")}</p>
                <Dialog.Close asChild>
                  <Link className="btn" href={`/${locale}`}>
                    {t("cart.emptyCta")}
                  </Link>
                </Dialog.Close>
              </div>
            )}

            {cart && cart.items.length > 0 && (
              <>
                {cart.remainingForFreeShipping !== null && cart.remainingForFreeShipping !== undefined && (
                  <div className="free-ship" aria-live="polite">
                    <p>{cart.remainingForFreeShipping > 0 ? t("cart.freeShippingLeft", { amount: money(cart.remainingForFreeShipping) }) : t("cart.freeShippingReached")}</p>
                    {cart.freeShippingThreshold ? (
                      <div className="progress" aria-hidden="true">
                        <span style={{ inlineSize: `${Math.min(100, (cart.total / cart.freeShippingThreshold) * 100)}%` }} />
                      </div>
                    ) : null}
                  </div>
                )}

                <ul className="cart-lines">
                  {cart.items.map((item) => (
                    <li key={item.lineId} className="cart-line" data-world={item.section === "honey" ? "honey" : item.section === "perfume" ? "perfume" : undefined}>
                      <div className="cart-thumb" aria-hidden="true" />
                      <div className="cart-line-main">
                        <Link href={`/${locale}/p/${item.slug}`} className="cart-line-name" onClick={() => setOpen(false)}>
                          {item.name}
                        </Link>
                        <span className="muted">{item.label}</span>
                        {item.components.length > 0 && <span className="muted small">{item.components.map((c) => `${c.name} ${c.label}`).join(" + ")}</span>}
                        {item.giftMessage && <span className="muted small">“{item.giftMessage}”</span>}
                        {item.priceChanged && <span className="notice small">{t("cart.priceChanged", { was: money(item.priceChanged.was), now: money(item.priceChanged.now) })}</span>}
                        {item.availability === "unavailable" && <span className="field-error">{t("cart.unavailable")}</span>}
                        {item.availability === "short" && <span className="field-error">{t("cart.short", { count: item.maxQty })}</span>}
                        {item.availability === "out" && <span className="field-error">{t("common.soldOut")}</span>}
                        <div className="cart-line-controls">
                          <label className="sr-only" htmlFor={`qty-${item.lineId}`}>
                            {t("common.qty")}
                          </label>
                          <select
                            id={`qty-${item.lineId}`}
                            className="select qty-select"
                            value={item.qty}
                            onChange={(e) => update.mutate({ lineId: item.lineId, qty: Number(e.target.value) }, { onError: fail })}
                          >
                            {Array.from({ length: Math.max(item.qty, item.isSample ? 3 : 10) }, (_, i) => i + 1).map((n) => (
                              <option key={n} value={n}>
                                {n}
                              </option>
                            ))}
                          </select>
                          <button type="button" className="btn btn-quiet" onClick={() => remove.mutate(item.lineId, { onError: fail })}>
                            {t("common.remove")}
                          </button>
                        </div>
                      </div>
                      <span className="price">{money(item.total)}</span>
                    </li>
                  ))}
                </ul>

                <form
                  className="coupon-row"
                  onSubmit={(e) => {
                    e.preventDefault();
                    setError(null);
                    coupon.mutate(code.trim() || null, { onError: fail });
                  }}
                >
                  <label className="sr-only" htmlFor="coupon">
                    {t("cart.coupon")}
                  </label>
                  <input id="coupon" className="input" placeholder={t("cart.coupon")} value={code} onChange={(e) => setCode(e.target.value)} autoCapitalize="characters" />
                  <button className="btn" type="submit">
                    {t("cart.couponApply")}
                  </button>
                </form>
                {cart.couponCode && !cart.couponError && <p className="notice-ok notice small">{t("cart.couponApplied", { code: cart.couponCode })}</p>}
                {cart.couponError && <p className="field-error">{errorText(cart.couponError)}</p>}
                {error && (
                  <p className="field-error" role="alert">
                    {error}
                  </p>
                )}
              </>
            )}
          </div>

          {cart && cart.items.length > 0 && (
            <div className="drawer-foot">
              <dl className="totals">
                <div>
                  <dt>{t("common.subtotal")}</dt>
                  <dd>{money(cart.subtotal)}</dd>
                </div>
                {cart.discount > 0 && (
                  <div>
                    <dt>{t("common.discount")}</dt>
                    <dd>−{money(cart.discount)}</dd>
                  </div>
                )}
                <div className="grand">
                  <dt>{t("common.total")}</dt>
                  <dd>{money(cart.total)}</dd>
                </div>
              </dl>
              <p className="muted small">{whatsapp ? t("whatsapp.note") : t("cart.shippingLater")}</p>
              {whatsapp ? (
                <>
                  <a
                    className="btn btn-buy btn-whatsapp"
                    target="_blank"
                    rel="noopener"
                    href={whatsappLink(
                      whatsapp,
                      t("whatsapp.cartMessage", {
                        lines: cart.items.map((i) => `• ${i.qty} × ${i.name} (${i.label}) = ${money(i.total)}`).join("\n"),
                        total: money(cart.total),
                      }),
                    )}
                  >
                    <WhatsAppIcon size={22} />
                    {t("whatsapp.orderCart")}
                  </a>
                  <Link className="btn btn-quiet" href={`/${locale}/checkout`} onClick={() => setOpen(false)}>
                    {t("whatsapp.orCard")}
                  </Link>
                </>
              ) : (
                <Link className="btn btn-buy" href={`/${locale}/checkout`} onClick={() => setOpen(false)}>
                  {t("cart.checkout")}
                </Link>
              )}
            </div>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
