"use client";

import { ApiError, type GiftSlot } from "@rahiq/api-client";
import { formatMoney } from "@rahiq/ui";
import { AnimatePresence, motion } from "motion/react";
import { useState } from "react";
import { useReducedMotion } from "@/lib/client/capability";
import { openCart, useCartMutations } from "@/lib/client/cart";
import { useErrorText, useLocale, useT } from "@/lib/client/i18n";

/**
 * The gift box builder (frontend-experience.md §3.7): choose the fragrance, the honey, write the message. Each choice
 * drops into the open box; the box closes with a ribbon at the end. Reduced motion: a clear list, no box.
 * The address and "hide prices" are asked at checkout, where they belong.
 */
export function GiftBoxBuilder({ boxVariantId, boxPrice, currency, slots }: { boxVariantId: string; boxPrice: number; currency: string; slots: GiftSlot[] }) {
  const t = useT();
  const locale = useLocale();
  const errorText = useErrorText();
  const reduced = useReducedMotion();
  const { add } = useCartMutations();
  const [chosen, setChosen] = useState<Record<string, string>>({});
  const [message, setMessage] = useState("");
  const [error, setError] = useState<string | null>(null);
  const money = (v: number) => formatMoney(v, currency, locale);

  const picks = slots.map((s) => s.options.find((o) => o.variantId === chosen[s.code])).filter((o) => o !== undefined);
  const complete = slots.every((s) => !s.required || chosen[s.code]);
  const total = boxPrice + picks.reduce((sum, o) => sum + (o.price ?? 0), 0);
  const steps = [t("gift.step1"), t("gift.step2")];

  const submit = () => {
    setError(null);
    add.mutate(
      { variantId: boxVariantId, qty: 1, components: slots.map((s) => chosen[s.code]).filter(Boolean), giftMessage: message.trim() || undefined },
      { onSuccess: () => setTimeout(openCart, reduced ? 0 : 700), onError: (e) => setError(errorText(e instanceof ApiError ? e.code : undefined)) },
    );
  };

  return (
    <div className="gift-builder">
      <div className="gift-steps">
        {slots.map((slot, i) => (
          <fieldset key={slot.code} className="gift-step" data-world={slot.allowedSections[0] === "honey" ? "honey" : "perfume"}>
            <legend className="h-small">
              {i + 1}. {steps[i] ?? slot.code}
            </legend>
            <div className="gift-options">
              {slot.options.map((o) => (
                <label key={o.variantId} className={`gift-option${chosen[slot.code] === o.variantId ? " is-chosen" : ""}`}>
                  <input type="radio" name={slot.code} value={o.variantId} checked={chosen[slot.code] === o.variantId} onChange={() => setChosen((c) => ({ ...c, [slot.code]: o.variantId }))} />
                  <span className="gift-option-name">{o.name}</span>
                  <span className="muted small">{o.label}</span>
                  {o.price != null && <span className="price small">{money(o.price)}</span>}
                </label>
              ))}
            </div>
          </fieldset>
        ))}

        <div className="field gift-step">
          <label htmlFor="gift-message" className="h-small">
            3. {t("gift.step3")}
          </label>
          <textarea id="gift-message" className="textarea" maxLength={300} value={message} onChange={(e) => setMessage(e.target.value)} placeholder={t("gift.messagePlaceholder")} />
          <span className="muted small">{message.length}/300</span>
          <p className="muted small">{t("gift.hidePricesHint")}</p>
        </div>
      </div>

      <aside className="gift-summary" aria-live="polite">
        {reduced ? (
          <ul className="gift-list">
            {picks.map((o) => (
              <li key={o.variantId}>
                {o.name} · {o.label}
              </li>
            ))}
          </ul>
        ) : (
          <div className={`gift-box-visual${complete ? " is-closed" : ""}`} aria-hidden="true">
            <div className="gift-box-inside">
              <AnimatePresence>
                {picks.map((o) => (
                  <motion.span
                    key={o.variantId}
                    className={`gift-item gift-item-${o.section}`}
                    initial={{ y: -120, opacity: 0, rotate: -6 }}
                    animate={{ y: 0, opacity: 1, rotate: 0 }}
                    exit={{ y: -60, opacity: 0 }}
                    transition={{ type: "spring", stiffness: 220, damping: 18 }}
                  >
                    {o.name}
                  </motion.span>
                ))}
              </AnimatePresence>
            </div>
            <span className="gift-lid" />
            <span className="gift-ribbon ribbon-perfume" />
            <span className="gift-ribbon ribbon-honey" />
          </div>
        )}
        <dl className="totals">
          <div>
            <dt>{t("gift.boxPrice")}</dt>
            <dd>{money(boxPrice)}</dd>
          </div>
          <div className="grand">
            <dt>{t("common.total")}</dt>
            <dd>{money(total)}</dd>
          </div>
        </dl>
        {complete && <p className="notice notice-ok small">{t("gift.closed")}</p>}
        <button type="button" className="btn btn-buy" disabled={!complete || add.isPending} onClick={submit}>
          {t("gift.addBox")}
        </button>
        {error && (
          <p className="field-error" role="alert">
            {error}
          </p>
        )}
      </aside>
    </div>
  );
}
