"use client";

import { ApiError, type Variant } from "@rahiq/api-client";
import { formatMoney } from "@rahiq/ui";
import { useRef, useState } from "react";
import { useReducedMotion } from "@/lib/client/capability";
import { openCart, useCartMutations } from "@/lib/client/cart";
import { useErrorText, useLocale, useT } from "@/lib/client/i18n";

/**
 * Size choice and add to cart. Without JavaScript the same form posts to /api/cart-form (level 4 of the
 * degradation ladder). With JavaScript the product "flies" to the cart icon once (motion that answers an action).
 */
export function BuyBox({ variants, currency, back, isPerfume }: { variants: Variant[]; currency: string; back: string; isPerfume: boolean }) {
  const t = useT();
  const locale = useLocale();
  const errorText = useErrorText();
  const reduced = useReducedMotion();
  const { add } = useCartMutations();
  const full = variants.filter((v) => !v.isSample);
  const sample = variants.find((v) => v.isSample);
  const [selected, setSelected] = useState(full.find((v) => v.availability !== "out")?.id ?? full[0]?.id);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const button = useRef<HTMLButtonElement>(null);
  const current = full.find((v) => v.id === selected);

  const fly = () => {
    const from = button.current?.getBoundingClientRect();
    const to = document.querySelector("[data-cart-target]")?.getBoundingClientRect();
    if (reduced || !from || !to) return;
    const dot = document.createElement("span");
    dot.className = "fly-dot";
    document.body.appendChild(dot);
    const animation = dot.animate(
      [
        { transform: `translate(${from.left + from.width / 2}px, ${from.top}px) scale(1)`, opacity: 1 },
        { transform: `translate(${to.left + to.width / 2}px, ${to.top + to.height / 2}px) scale(0.3)`, opacity: 0.2 },
      ],
      { duration: 650, easing: "cubic-bezier(0.22, 1, 0.36, 1)" },
    );
    animation.onfinish = () => dot.remove();
  };

  const submit = (event: React.FormEvent, variantId: string | undefined) => {
    event.preventDefault();
    if (!variantId) return;
    setMessage(null);
    add.mutate(
      { variantId, qty: 1 },
      {
        onSuccess: () => {
          fly();
          setMessage({ ok: true, text: t("product.added") });
          setTimeout(openCart, reduced ? 0 : 500);
        },
        onError: (e) => setMessage({ ok: false, text: errorText(e instanceof ApiError ? e.code : undefined) }),
      },
    );
  };

  return (
    <div className="buy-box">
      <form action="/api/cart-form" method="post" onSubmit={(e) => submit(e, selected)}>
        <input type="hidden" name="back" value={back} />
        <fieldset className="sizes">
          <legend>{t("product.chooseSize")}</legend>
          {full.map((v) => (
            <label key={v.id} className={`size${v.availability === "out" ? " size-out" : ""}`}>
              <input type="radio" name="variantId" value={v.id} checked={selected === v.id} onChange={() => setSelected(v.id)} disabled={v.availability === "out"} />
              <span className="size-label">{v.label}</span>
              {v.price != null && <span className="size-price">{formatMoney(v.price, currency, locale)}</span>}
              {v.availability === "low" && <span className="size-note">{t("common.lowStock")}</span>}
              {v.availability === "out" && <span className="size-note">{t("common.soldOut")}</span>}
            </label>
          ))}
        </fieldset>
        {current?.previousPrice ? (
          <p className="muted small">
            {t("product.previousPrice")}: <s>{formatMoney(current.previousPrice, currency, locale)}</s>
          </p>
        ) : null}
        <button ref={button} className="btn btn-buy" type="submit" disabled={!current || current.availability === "out" || add.isPending}>
          {current && current.availability !== "out" ? `${t("product.addToCart")} · ${current.price != null ? formatMoney(current.price, currency, locale) : ""}` : t("product.unavailable")}
        </button>
      </form>

      {isPerfume && sample && (
        <form action="/api/cart-form" method="post" onSubmit={(e) => submit(e, sample.id)} className="sample-form">
          <input type="hidden" name="back" value={back} />
          <input type="hidden" name="variantId" value={sample.id} />
          <button className="btn" type="submit" disabled={sample.availability === "out" || add.isPending}>
            {t("product.trySample")}
            {sample.price != null ? ` · ${formatMoney(sample.price, currency, locale)}` : ""}
          </button>
          <p className="muted small">{t("product.sampleHint")}</p>
        </form>
      )}

      <p aria-live="polite" className={message ? (message.ok ? "notice notice-ok small" : "field-error") : "sr-only"}>
        {message?.text}
      </p>
    </div>
  );
}
