"use client";

import { useEffect, useRef, useState } from "react";
import { openCart, useCart } from "@/lib/client/cart";
import { useT } from "@/lib/client/i18n";

/** The count pulses once when something is added (frontend-experience.md §6): motion that answers an action. */
export function CartButton() {
  const t = useT();
  const { data } = useCart();
  const count = data?.itemCount ?? 0;
  const [pulse, setPulse] = useState(false);
  const previous = useRef(count);

  useEffect(() => {
    if (count > previous.current) {
      setPulse(true);
      const timer = setTimeout(() => setPulse(false), 500);
      previous.current = count;
      return () => clearTimeout(timer);
    }

    previous.current = count;
  }, [count]);

  return (
    <button type="button" className="btn btn-quiet cart-button" onClick={openCart} aria-label={`${t("nav.cart")}: ${t("common.items", { count })}`} data-cart-target>
      <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
        <path d="M5 8h14l-1.2 11.2a2 2 0 0 1-2 1.8H8.2a2 2 0 0 1-2-1.8Z" />
        <path d="M9 8V6a3 3 0 0 1 6 0v2" />
      </svg>
      {count > 0 && <span className={`cart-count${pulse ? " pulse" : ""}`}>{count}</span>}
    </button>
  );
}
