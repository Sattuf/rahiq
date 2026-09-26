"use client";

import type { OrderStatus } from "@rahiq/api-client";
import Link from "next/link";
import { useEffect, useState } from "react";
import { bff } from "@/lib/client/bff";
import { useLocale, useT } from "@/lib/client/i18n";

type State = "checking" | "confirmed" | "failed" | "pending";

/**
 * "We are verifying your payment" (commerce-flows.md §5): the browser's return proves nothing, so this page asks the
 * server every two seconds, for up to thirty, whether the provider's signed notification has confirmed the order.
 */
export function PaymentResult({ number }: { number: string }) {
  const t = useT();
  const locale = useLocale();
  const [state, setState] = useState<State>("checking");

  useEffect(() => {
    let tries = 0;
    let timer: ReturnType<typeof setTimeout>;
    const poll = async () => {
      tries++;
      try {
        const status = await bff<OrderStatus>(`orders/${encodeURIComponent(number)}/status`, { locale });
        if (["confirmed", "preparing", "shipped", "delivered"].includes(status.status)) return setState("confirmed");
        if (status.status === "cancelled" || status.paymentStatus === "failed") return setState("failed");
      } catch {
        // Keep polling; a transient error is not a result.
      }

      if (tries >= 15) setState("pending");
      else timer = setTimeout(poll, 2000);
    };
    void poll();
    return () => clearTimeout(timer);
  }, [number, locale]);

  return (
    <div className="container narrow section result" aria-live="polite">
      {state === "checking" && (
        <>
          <div className="spinner" aria-hidden="true" />
          <h1>{t("result.checking")}</h1>
          <p className="lede">{t("result.checkingText")}</p>
        </>
      )}
      {state === "confirmed" && (
        <>
          <h1>{t("result.confirmed")}</h1>
          <p className="lede">{t("result.confirmedText", { number })}</p>
          <p>
            <Link className="btn btn-buy" href={`/${locale}/track?order=${encodeURIComponent(number)}`}>
              {t("result.viewOrder")}
            </Link>
          </p>
          <p>
            <Link href={`/${locale}/account`}>{t("result.saveDetails")} →</Link>
          </p>
        </>
      )}
      {state === "failed" && (
        <>
          <h1>{t("result.failed")}</h1>
          <p className="lede">{t("result.failedText")}</p>
          <Link className="btn" href={`/${locale}/checkout`}>
            {t("result.backToCart")}
          </Link>
        </>
      )}
      {state === "pending" && (
        <>
          <h1>{t("result.pending")}</h1>
          <p className="lede">{t("result.pendingText")}</p>
          <Link className="btn" href={`/${locale}/track?order=${encodeURIComponent(number)}`}>
            {t("result.viewOrder")}
          </Link>
        </>
      )}
    </div>
  );
}
