"use client";

import { ApiError, type OrderView } from "@rahiq/api-client";
import { useState } from "react";
import { bff } from "@/lib/client/bff";
import { useErrorText, useLocale, useT } from "@/lib/client/i18n";
import { OrderDetail } from "./OrderDetail";
import { ReturnForm } from "./ReturnForm";

/** Guest tracking needs the order number and the e-mail together (security.md §3); the API rate-limits it. */
export function TrackOrder({ initialNumber }: { initialNumber?: string }) {
  const t = useT();
  const locale = useLocale();
  const errorText = useErrorText();
  const [number, setNumber] = useState(initialNumber ?? "");
  const [email, setEmail] = useState("");
  const [order, setOrder] = useState<OrderView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [showReturn, setShowReturn] = useState(false);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setError(null);
    try {
      setOrder(await bff<OrderView>("orders/lookup", { method: "POST", body: { number: number.trim(), email: email.trim() }, locale }));
    } catch (e) {
      setError(e instanceof ApiError && e.status === 404 ? t("track.notFound") : errorText(e instanceof ApiError ? e.code : undefined));
    }
  };

  return (
    <div>
      <form className="track-form" onSubmit={submit}>
        <div className="field">
          <label htmlFor="number">{t("track.number")}</label>
          <input id="number" className="input" required value={number} onChange={(e) => setNumber(e.target.value)} autoCapitalize="characters" />
        </div>
        <div className="field">
          <label htmlFor="email">{t("track.email")}</label>
          <input id="email" className="input" type="email" required value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="email" />
        </div>
        <button className="btn" type="submit">
          {t("track.submit")}
        </button>
      </form>
      {error && (
        <p className="field-error" role="alert">
          {error}
        </p>
      )}
      {order && (
        <OrderDetail order={order}>
          {order.canRequestReturn && !showReturn && (
            <button className="btn" type="button" onClick={() => setShowReturn(true)}>
              {t("order.requestReturn")}
            </button>
          )}
          {showReturn && <ReturnForm order={order} email={email} />}
        </OrderDetail>
      )}
    </div>
  );
}
