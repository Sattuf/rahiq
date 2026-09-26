"use client";

import { ApiError, type CheckoutView, idempotencyKey, type PayResult, type Province } from "@rahiq/api-client";
import { formatMoney } from "@rahiq/ui";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { useForm } from "react-hook-form";
import { bff } from "@/lib/client/bff";
import { useErrorText, useLocale, useT } from "@/lib/client/i18n";

const PHONE = /^\+?[0-9 ()-]{10,20}$/;
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/;

// Plain react-hook-form rules instead of a schema library: checkout must stay under its 120 KB script budget.
type Form = {
  email: string;
  phone: string;
  fullName: string;
  provinceCode: number;
  district: string;
  neighbourhood?: string;
  line1: string;
  line2?: string;
  postalCode?: string;
  company: boolean;
  companyName?: string;
  taxOffice?: string;
  taxNumber?: string;
  isGift: boolean;
  giftMessage?: string;
  hidePrices: boolean;
  marketingConsent: boolean;
};

/**
 * The three steps on one calm page (commerce-flows.md §2). Every amount shown comes from the server's quote;
 * the contracts shown are exactly the ones whose hash is accepted and re-checked at pay.
 */
export function Checkout() {
  const t = useT();
  const locale = useLocale();
  const router = useRouter();
  const errorText = useErrorText();
  const [id, setId] = useState<string | null>(null);
  const [view, setView] = useState<CheckoutView | null>(null);
  const [provinces, setProvinces] = useState<Province[]>([]);
  const [step, setStep] = useState<1 | 2 | 3>(1);
  const [method, setMethod] = useState<"card" | "cod">("card");
  const [accepted, setAccepted] = useState(false);
  const [contract, setContract] = useState<"pre" | "sales" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [paying, setPaying] = useState(false);
  const payKey = useRef<string | null>(null);
  const started = useRef(false);
  const [refreshing, setRefreshing] = useState(false);

  const form = useForm<Form>({ defaultValues: { company: false, isGift: false, hidePrices: false, marketingConsent: false, provinceCode: 0 } });
  const required = { required: t("checkout.fieldRequired"), validate: (v: unknown) => String(v ?? "").trim().length > 0 || t("checkout.fieldRequired") };
  const { register, handleSubmit, watch, reset, formState } = form;
  const errors = formState.errors;

  useEffect(() => {
    if (started.current) return; // Start the checkout once, not on every render.
    started.current = true;
    void (async () => {
      try {
        const checkoutId = await bff<string>("checkout", { method: "POST", body: {}, locale });
        setId(checkoutId);
        const [v, p] = await Promise.all([bff<CheckoutView>(`checkout/${checkoutId}?paymentMethod=card`, { locale }), bff<Province[]>("shipping/provinces")]);
        setView(v);
        setProvinces(p);
        if (v.shippingAddress && v.email) {
          const a = v.shippingAddress;
          reset({
            email: v.email, phone: v.phone ?? a.phone, fullName: a.fullName, provinceCode: a.provinceCode, district: a.district, neighbourhood: a.neighbourhood ?? "",
            line1: a.line1, line2: a.line2 ?? "", postalCode: a.postalCode ?? "", company: !!v.billingAddress?.taxNumber, companyName: v.billingAddress?.companyName ?? "",
            taxOffice: v.billingAddress?.taxOffice ?? "", taxNumber: v.billingAddress?.taxNumber ?? "", isGift: v.isGift, giftMessage: v.giftMessage ?? "", hidePrices: v.hidePrices,
            marketingConsent: v.marketingConsent,
          });
        }
      } catch (e) {
        if (e instanceof ApiError && e.code === "checkout.cart_empty") router.replace(`/${locale}`);
        else setError(errorText(e instanceof ApiError ? e.code : undefined));
      }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // The quote and the contracts depend on the payment method (COD fee): pay stays locked until they match.
  const refresh = async (m: "card" | "cod") => {
    if (!id) return;
    setRefreshing(true);
    try {
      setView(await bff<CheckoutView>(`checkout/${id}?paymentMethod=${m}`, { locale }));
    } finally {
      setRefreshing(false);
    }
  };

  const save = handleSubmit(async (values) => {
    if (!id) return;
    setError(null);
    const address = {
      fullName: values.fullName, phone: values.phone, provinceCode: Number(values.provinceCode), district: values.district,
      neighbourhood: values.neighbourhood || null, line1: values.line1, line2: values.line2 || null, postalCode: values.postalCode || null,
    };
    try {
      const v = await bff<CheckoutView>(`checkout/${id}`, {
        method: "PUT",
        locale,
        body: {
          email: values.email, phone: values.phone, shippingAddress: address,
          billingAddress: values.company ? { ...address, companyName: values.companyName, taxOffice: values.taxOffice, taxNumber: values.taxNumber } : null,
          isGift: values.isGift, giftMessage: values.isGift ? values.giftMessage : null, hidePrices: values.isGift && values.hidePrices,
          shippingMethod: view?.shippingMethod ?? null, marketingConsent: values.marketingConsent,
        },
      });
      setView(v);
      setAccepted(false);
      setStep(2);
      await refresh(method);
    } catch (e) {
      setError(errorText(e instanceof ApiError ? e.code : undefined));
    }
  });

  const pay = async () => {
    if (!id || !view?.contracts) return;
    setError(null);
    setPaying(true);
    payKey.current ??= idempotencyKey(); // One key per intention: a retry after a network error reuses it.
    try {
      const result = await bff<PayResult>(`checkout/${id}/pay`, { method: "POST", idempotent: payKey.current, locale, body: { paymentMethod: method, acceptedContractsHash: view.contracts.hash } });
      if (result.redirectUrl) {
        window.location.assign(result.redirectUrl); // The provider's hosted page: card data never touches our servers (Law 7).
        return;
      }
      router.push(`/${locale}/checkout/result?order=${encodeURIComponent(result.number)}`);
    } catch (e) {
      const code = e instanceof ApiError ? e.code : undefined;
      if (code !== "idempotency.in_progress") payKey.current = null;
      if (code === "checkout.contracts_changed" || code === "stock.insufficient") {
        setAccepted(false);
        await refresh(method);
      }
      setError(errorText(code));
      setPaying(false);
    }
  };

  const q = view?.quote;
  const money = (v: number) => formatMoney(v, q?.currency ?? "TRY", locale);
  const isGift = watch("isGift");
  const company = watch("company");
  const fieldError = (name: keyof Form) => errors[name]?.message ? <span className="field-error" id={`${name}-error`}>{String(errors[name]?.message)}</span> : null;
  const aria = (name: keyof Form) => ({ "aria-invalid": !!errors[name], "aria-describedby": errors[name] ? `${name}-error` : undefined });

  return (
    <div className="container checkout-grid">
      <div className="checkout-steps">
        <h1>{t("checkout.title")}</h1>
        {error && (
          <p className="notice notice-danger" role="alert">
            {error}
          </p>
        )}

        <section className="checkout-step" aria-current={step === 1 ? "step" : undefined}>
          <div className="step-head">
            <h2>1. {t("checkout.step1")}</h2>
            {step > 1 && (
              <button type="button" className="btn btn-quiet" onClick={() => setStep(1)}>
                {t("checkout.edit")}
              </button>
            )}
          </div>
          {step === 1 ? (
            <form onSubmit={save} noValidate className="form-grid">
              <div className="field span-2">
                <label htmlFor="email">{t("checkout.email")}</label>
                <input id="email" className="input" type="email" autoComplete="email" {...register("email", { required: t("checkout.fieldRequired"), pattern: { value: EMAIL, message: t("checkout.emailInvalid") } })} {...aria("email")} />
                {fieldError("email")}
              </div>
              <div className="field">
                <label htmlFor="fullName">{t("checkout.fullName")}</label>
                <input id="fullName" className="input" autoComplete="name" {...register("fullName", { ...required, maxLength: 120 })} {...aria("fullName")} />
                {fieldError("fullName")}
              </div>
              <div className="field">
                <label htmlFor="phone">{t("checkout.phone")}</label>
                <input id="phone" className="input" type="tel" autoComplete="tel" inputMode="tel" {...register("phone", { required: t("checkout.fieldRequired"), pattern: { value: PHONE, message: t("checkout.phoneInvalid") } })} {...aria("phone")} />
                {fieldError("phone")}
              </div>
              <div className="field">
                <label htmlFor="provinceCode">{t("checkout.province")}</label>
                <select id="provinceCode" className="select" {...register("provinceCode", { valueAsNumber: true, validate: (v) => (v >= 1 && v <= 81) || t("checkout.fieldRequired") })} {...aria("provinceCode")}>
                  <option value={0}>{t("checkout.chooseProvince")}</option>
                  {provinces.map((p) => (
                    <option key={p.code} value={p.code}>
                      {p.name}
                    </option>
                  ))}
                </select>
                {fieldError("provinceCode")}
              </div>
              <div className="field">
                <label htmlFor="district">{t("checkout.district")}</label>
                <input id="district" className="input" autoComplete="address-level2" {...register("district", { ...required, maxLength: 80 })} {...aria("district")} />
                {fieldError("district")}
              </div>
              <div className="field">
                <label htmlFor="neighbourhood">
                  {t("checkout.neighbourhood")} <span className="muted">({t("common.optional")})</span>
                </label>
                <input id="neighbourhood" className="input" {...register("neighbourhood")} />
              </div>
              <div className="field">
                <label htmlFor="postalCode">
                  {t("checkout.postalCode")} <span className="muted">({t("common.optional")})</span>
                </label>
                <input id="postalCode" className="input" autoComplete="postal-code" inputMode="numeric" {...register("postalCode")} />
              </div>
              <div className="field span-2">
                <label htmlFor="line1">{t("checkout.line1")}</label>
                <input id="line1" className="input" autoComplete="street-address" {...register("line1", { ...required, maxLength: 200 })} {...aria("line1")} />
                {fieldError("line1")}
              </div>
              <div className="field span-2">
                <label htmlFor="line2">
                  {t("checkout.line2")} <span className="muted">({t("common.optional")})</span>
                </label>
                <input id="line2" className="input" {...register("line2")} />
              </div>

              <label className="check span-2">
                <input type="checkbox" {...register("company")} />
                {t("checkout.company")}
              </label>
              {company && (
                <>
                  <div className="field span-2">
                    <label htmlFor="companyName">{t("checkout.companyName")}</label>
                    <input id="companyName" className="input" autoComplete="organization" {...register("companyName")} />
                  </div>
                  <div className="field">
                    <label htmlFor="taxOffice">{t("checkout.taxOffice")}</label>
                    <input id="taxOffice" className="input" {...register("taxOffice")} />
                  </div>
                  <div className="field">
                    <label htmlFor="taxNumber">{t("checkout.taxNumber")}</label>
                    <input id="taxNumber" className="input" inputMode="numeric" {...register("taxNumber", { validate: (v) => !watch("company") || /^[0-9]{10,11}$/.test(v ?? "") || t("checkout.taxInvalid") })} {...aria("taxNumber")} />
                    {fieldError("taxNumber")}
                  </div>
                </>
              )}

              <label className="check span-2">
                <input type="checkbox" {...register("isGift")} />
                {t("checkout.isGift")}
              </label>
              {isGift && (
                <>
                  <div className="field span-2">
                    <label htmlFor="giftMessage">{t("checkout.giftMessage")}</label>
                    <textarea id="giftMessage" className="textarea" maxLength={300} {...register("giftMessage", { maxLength: 300 })} />
                  </div>
                  <label className="check span-2">
                    <input type="checkbox" {...register("hidePrices")} />
                    {t("checkout.hidePrices")}
                  </label>
                </>
              )}

              <label className="check span-2 small">
                <input type="checkbox" {...register("marketingConsent")} />
                {t("checkout.marketing")}
              </label>

              <button className="btn btn-buy span-2" type="submit" disabled={formState.isSubmitting || !id}>
                {t("checkout.saveAddress")}
              </button>
            </form>
          ) : (
            view?.shippingAddress && (
              <p className="muted">
                {view.email} · {view.shippingAddress.fullName}, {view.shippingAddress.line1}, {view.shippingAddress.district} / {view.shippingAddress.provinceName}
              </p>
            )
          )}
        </section>

        <section className="checkout-step" aria-current={step === 2 ? "step" : undefined}>
          <h2>2. {t("checkout.step2")}</h2>
          {step >= 2 && q && (
            <>
              {q.shippingOptions.map((o) => (
                <label key={o.method} className="option-card">
                  <input type="radio" name="shipping" defaultChecked readOnly />
                  <span>
                    <strong>{t("checkout.standard")}</strong>
                    <span className="muted small">{t("checkout.eta")}</span>
                  </span>
                  <span className="price">{o.isFree || o.amount === 0 ? t("common.free") : money(o.amount)}</span>
                </label>
              ))}
              {q.hasFlammable && <p className="muted small">{t("checkout.flammableNote")}</p>}
              {step === 2 && (
                <button className="btn btn-buy" type="button" onClick={() => setStep(3)}>
                  {t("common.continue")}
                </button>
              )}
            </>
          )}
        </section>

        <section className="checkout-step" aria-current={step === 3 ? "step" : undefined}>
          <h2>3. {t("checkout.step3")}</h2>
          {step === 3 && q && view && (
            <>
              <fieldset className="pay-methods">
                <legend className="sr-only">{t("checkout.step3")}</legend>
                <label className="option-card">
                  <input
                    type="radio"
                    name="method"
                    checked={method === "card"}
                    onChange={() => {
                      setMethod("card");
                      setAccepted(false);
                      void refresh("card");
                    }}
                  />
                  <span>
                    <strong>{t("checkout.card")}</strong>
                    <span className="muted small">{t("checkout.cardHint")}</span>
                  </span>
                </label>
                <label className="option-card" aria-disabled={!view.codAvailable}>
                  <input
                    type="radio"
                    name="method"
                    disabled={!view.codAvailable && method !== "cod"}
                    checked={method === "cod"}
                    onChange={() => {
                      setMethod("cod");
                      setAccepted(false);
                      void refresh("cod");
                    }}
                  />
                  <span>
                    <strong>{t("checkout.cod")}</strong>
                    <span className="muted small">
                      {view.codAvailable ? t("checkout.codHint", { fee: money(q.codFee || 4990) }) : view.codUnavailableReason ? t(`checkout.codReasons.${view.codUnavailableReason}`) : t("checkout.codUnavailable")}
                    </span>
                  </span>
                </label>
              </fieldset>

              {view.contracts && (
                <div className="contracts">
                  <p className="small">
                    {t("checkout.contracts")}:{" "}
                    <button type="button" className="link-button" onClick={() => setContract("pre")}>
                      {t("checkout.preInfo")}
                    </button>{" "}
                    ·{" "}
                    <button type="button" className="link-button" onClick={() => setContract("sales")}>
                      {t("checkout.distanceSales")}
                    </button>
                  </p>
                  <label className="check">
                    <input type="checkbox" checked={accepted} onChange={(e) => setAccepted(e.target.checked)} />
                    {t("checkout.accept")}
                  </label>
                </div>
              )}

              <button className="btn btn-buy pay-button" type="button" disabled={!accepted || paying || refreshing || !view.readyToPay} onClick={pay}>
                {paying ? t("checkout.processing") : method === "card" ? t("checkout.pay", { amount: money(q.total) }) : t("checkout.placeCod", { amount: money(q.total) })}
              </button>
            </>
          )}
        </section>
      </div>

      <aside className="checkout-summary" aria-label={t("checkout.summary")}>
        <h2 className="h-small">{t("checkout.summary")}</h2>
        {q && (
          <>
            <ul className="summary-lines">
              {q.lines.map((l, i) => (
                <li key={`${l.variantId}-${i}`}>
                  <span>
                    {l.groupLabel ? <span className="muted small">{l.groupLabel}: </span> : null}
                    {l.name} <span className="muted">{l.variantLabel} × {l.qty}</span>
                  </span>
                  <span className="price">{money(l.lineTotal)}</span>
                </li>
              ))}
            </ul>
            <dl className="totals">
              <div>
                <dt>{t("common.subtotal")}</dt>
                <dd>{money(q.subtotal)}</dd>
              </div>
              {q.discount > 0 && (
                <div>
                  <dt>{t("common.discount")}</dt>
                  <dd>−{money(q.discount)}</dd>
                </div>
              )}
              <div>
                <dt>{t("common.shipping")}</dt>
                <dd>{step === 1 && !view?.shippingAddress ? "—" : q.shipping === 0 ? t("common.free") : money(q.shipping)}</dd>
              </div>
              {q.codFee > 0 && (
                <div>
                  <dt>{t("common.codFee")}</dt>
                  <dd>{money(q.codFee)}</dd>
                </div>
              )}
              <div className="grand">
                <dt>{t("common.total")}</dt>
                <dd>{money(q.total)}</dd>
              </div>
            </dl>
            <p className="muted small">{t("common.vatIncluded")}</p>
          </>
        )}
      </aside>

      {contract && view?.contracts && (
        <div className="overlay" onClick={() => setContract(null)} role="presentation">
          <div className="dialog" role="dialog" aria-modal="true" aria-label={contract === "pre" ? t("checkout.preInfo") : t("checkout.distanceSales")} onClick={(e) => e.stopPropagation()}>
            <button type="button" className="btn btn-quiet dialog-close" onClick={() => setContract(null)} autoFocus>
              {t("common.close")}
            </button>
            {/* The exact document whose hash is accepted, isolated in a sandboxed frame. */}
            <iframe title={contract === "pre" ? t("checkout.preInfo") : t("checkout.distanceSales")} className="contract-frame" sandbox="" srcDoc={contract === "pre" ? view.contracts.preInformationHtml : view.contracts.distanceSalesHtml} />
          </div>
        </div>
      )}
    </div>
  );
}
