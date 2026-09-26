"use client";

import { ApiError, type OrderSummary, type OrderView, type Profile } from "@rahiq/api-client";
import { formatDate, formatMoney } from "@rahiq/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { bff } from "@/lib/client/bff";
import { openCart } from "@/lib/client/cart";
import { useErrorText, useLocale, useT } from "@/lib/client/i18n";
import { OrderDetail } from "./OrderDetail";

export function SignIn({ onSignedIn }: { onSignedIn: () => void }) {
  const t = useT();
  const locale = useLocale();
  const errorText = useErrorText();
  const [email, setEmail] = useState("");
  const [code, setCode] = useState("");
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const run = async (action: () => Promise<void>) => {
    setError(null);
    setBusy(true);
    try {
      await action();
    } catch (e) {
      setError(errorText(e instanceof ApiError ? e.code : undefined));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="sign-in">
      <h2>{t("account.signIn")}</h2>
      <p className="muted">{t("account.signInIntro")}</p>
      {!sent ? (
        <form onSubmit={(e) => { e.preventDefault(); void run(async () => { await bff("auth/otp/request", { method: "POST", body: { email }, locale }); setSent(true); }); }}>
          <div className="field">
            <label htmlFor="email">{t("checkout.email")}</label>
            <input id="email" className="input" type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
          </div>
          <button className="btn" type="submit" disabled={busy}>
            {t("account.sendCode")}
          </button>
        </form>
      ) : (
        <form onSubmit={(e) => { e.preventDefault(); void run(async () => { await bff("auth/otp/verify", { method: "POST", body: { email, code }, locale }); onSignedIn(); }); }}>
          <p className="notice small">{t("account.codeSent", { email })}</p>
          <div className="field">
            <label htmlFor="code">{t("account.code")}</label>
            <input id="code" className="input otp" inputMode="numeric" autoComplete="one-time-code" pattern="[0-9]{6}" maxLength={6} required value={code} onChange={(e) => setCode(e.target.value)} />
          </div>
          <button className="btn btn-buy" type="submit" disabled={busy}>
            {t("account.verify")}
          </button>
        </form>
      )}
      {error && (
        <p className="field-error" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}

export function Account({ signedIn }: { signedIn: boolean }) {
  const t = useT();
  const locale = useLocale();
  const client = useQueryClient();
  const [isIn, setIsIn] = useState(signedIn);
  const [open, setOpen] = useState<string | null>(null);
  const profile = useQuery({ queryKey: ["me"], queryFn: () => bff<Profile>("me", { locale }), enabled: isIn });
  const orders = useQuery({ queryKey: ["me", "orders"], queryFn: () => bff<OrderSummary[]>("me/orders", { locale }), enabled: isIn });
  const detail = useQuery({ queryKey: ["me", "orders", open], queryFn: () => bff<OrderView>(`me/orders/${open}`, { locale }), enabled: isIn && !!open });
  const [saved, setSaved] = useState(false);

  if (!isIn || profile.error) {
    return (
      <SignIn
        onSignedIn={() => {
          setIsIn(true);
          void client.invalidateQueries();
        }}
      />
    );
  }

  const p = profile.data;
  const money = (v: number, c: string) => formatMoney(v, c, locale);

  return (
    <div className="account">
      <div className="account-head">
        <p className="muted">{p?.email}</p>
        <button
          type="button"
          className="btn btn-quiet"
          onClick={async () => {
            await bff("auth/logout", { method: "POST" });
            window.location.reload();
          }}
        >
          {t("account.signOut")}
        </button>
      </div>

      <section>
        <h2>{t("account.orders")}</h2>
        {orders.data?.length === 0 && <p className="muted">{t("account.noOrders")}</p>}
        <ul className="order-list">
          {orders.data?.map((o) => (
            <li key={o.id}>
              <button type="button" className="order-row" aria-expanded={open === o.number} onClick={() => setOpen(open === o.number ? null : o.number)}>
                <span>
                  <strong>{o.number}</strong> <span className="muted small">{formatDate(o.placedAt, locale)}</span>
                </span>
                <span className="muted">{o.firstItem}</span>
                <span className={`status status-${o.status}`}>{t(`order.statuses.${o.status}`)}</span>
                <span className="price">{money(o.total, o.currency)}</span>
              </button>
              {open === o.number && detail.data && (
                <OrderDetail order={detail.data}>
                  <button
                    type="button"
                    className="btn"
                    onClick={async () => {
                      await bff(`me/orders/${o.number}/reorder`, { method: "POST" });
                      await client.invalidateQueries({ queryKey: ["cart"] });
                      openCart();
                    }}
                  >
                    {t("order.reorder")}
                  </button>
                </OrderDetail>
              )}
            </li>
          ))}
        </ul>
      </section>

      {p && (
        <section>
          <h2>{t("account.profile")}</h2>
          <form
            className="profile-form"
            onSubmit={async (e) => {
              e.preventDefault();
              const form = new FormData(e.currentTarget);
              await bff("me", { method: "PUT", body: { name: form.get("name"), phone: form.get("phone"), locale: form.get("locale") } });
              setSaved(true);
            }}
          >
            <div className="field">
              <label htmlFor="name">{t("account.name")}</label>
              <input id="name" name="name" className="input" defaultValue={p.name ?? ""} autoComplete="name" />
            </div>
            <div className="field">
              <label htmlFor="phone">{t("checkout.phone")}</label>
              <input id="phone" name="phone" className="input" defaultValue={p.phone ?? ""} autoComplete="tel" />
            </div>
            <div className="field">
              <label htmlFor="locale">{t("account.language")}</label>
              <select id="locale" name="locale" className="select" defaultValue={p.locale}>
                <option value="tr">Türkçe</option>
                <option value="ar">العربية</option>
                <option value="en">English</option>
              </select>
            </div>
            <button className="btn" type="submit">
              {t("common.save")}
            </button>
            {saved && <span className="muted small"> {t("common.saved")}</span>}
          </form>

          <h3>{t("account.consents")}</h3>
          {(["email", "sms"] as const).map((channel) => (
            <label key={channel} className="check">
              <input
                type="checkbox"
                defaultChecked={channel === "email" ? p.marketingEmail : p.marketingSms}
                onChange={(e) => void bff("me/consents", { method: "PUT", body: { channel, granted: e.target.checked } })}
              />
              {channel === "email" ? t("account.consentEmail") : t("account.consentSms")}
            </label>
          ))}

          <h3>{t("account.privacy")}</h3>
          <div className="privacy-actions">
            <button
              type="button"
              className="btn"
              onClick={async () => {
                const data = await bff<unknown>("me/export");
                const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: "application/json" }));
                const a = document.createElement("a");
                a.href = url;
                a.download = "rahiq-my-data.json";
                a.click();
                URL.revokeObjectURL(url);
              }}
            >
              {t("account.exportData")}
            </button>
            <button
              type="button"
              className="btn btn-danger"
              onClick={async () => {
                if (!window.confirm(t("account.deleteConfirm"))) return;
                await bff("me", { method: "DELETE" });
                await bff("auth/logout", { method: "POST" });
                window.location.assign(`/${locale}`);
              }}
            >
              {t("account.deleteAccount")}
            </button>
          </div>
        </section>
      )}
    </div>
  );
}
