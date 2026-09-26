"use client";

import { ApiError, type OrderView } from "@rahiq/api-client";
import { useState } from "react";
import { bff } from "@/lib/client/bff";
import { useErrorText, useLocale, useT } from "@/lib/client/i18n";

/**
 * Return request (commerce-flows.md §10). The rules are applied by the server, but the form already tells the
 * truth: food lines show "only if damaged or wrong", and damage needs photos.
 */
export function ReturnForm({ order, email }: { order: OrderView; email?: string }) {
  const t = useT();
  const locale = useLocale();
  const errorText = useErrorText();
  const [kind, setKind] = useState<"withdrawal" | "damaged" | "wrong_item">("damaged");
  const [reason, setReason] = useState("");
  const [lines, setLines] = useState<Record<string, { qty: number; sealIntact: boolean }>>({});
  const [photos, setPhotos] = useState<string[]>([]);
  const [state, setState] = useState<"idle" | "sending" | "done">("idle");
  const [error, setError] = useState<string | null>(null);

  const upload = async (files: FileList | null) => {
    for (const file of Array.from(files ?? []).slice(0, 4)) {
      const form = new FormData();
      form.append("file", file);
      try {
        const key = await bff<string>("orders/returns/photos", { method: "POST", form });
        setPhotos((p) => [...p, key]);
      } catch (e) {
        setError(errorText(e instanceof ApiError ? e.code : undefined));
      }
    }
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setError(null);
    setState("sending");
    try {
      await bff("orders/returns", {
        method: "POST",
        locale,
        body: {
          number: order.number,
          email,
          kind,
          reason,
          photoKeys: photos,
          lines: Object.entries(lines).filter(([, v]) => v.qty > 0).map(([lineId, v]) => ({ lineId, qty: v.qty, sealIntact: v.sealIntact })),
        },
      });
      setState("done");
    } catch (e) {
      setError(errorText(e instanceof ApiError ? e.code : undefined));
      setState("idle");
    }
  };

  if (state === "done") return <p className="notice notice-ok">{t("returns.done")}</p>;

  return (
    <form className="return-form" onSubmit={submit}>
      <h3>{t("returns.title")}</h3>
      <fieldset>
        <legend>{t("returns.kind")}</legend>
        {(["damaged", "wrong_item", "withdrawal"] as const).map((k) => (
          <label key={k} className="check">
            <input type="radio" name="kind" checked={kind === k} onChange={() => setKind(k)} />
            {t(`returns.${k}`)}
          </label>
        ))}
      </fieldset>

      <fieldset>
        <legend>{t("order.items")}</legend>
        {order.lines.map((l) => {
          const blocked = kind === "withdrawal" && !l.returnable;
          return (
            <div key={l.id} className="return-line">
              <label className="check">
                <input
                  type="checkbox"
                  disabled={blocked}
                  checked={(lines[l.id]?.qty ?? 0) > 0}
                  onChange={(e) => setLines((s) => ({ ...s, [l.id]: { qty: e.target.checked ? l.qty : 0, sealIntact: s[l.id]?.sealIntact ?? true } }))}
                />
                {l.name} · {l.variantLabel} × {l.qty}
              </label>
              {blocked && <span className="muted small">{t("returns.notReturnable")}</span>}
              {kind === "withdrawal" && l.returnable && (lines[l.id]?.qty ?? 0) > 0 && (
                <label className="check small">
                  <input type="checkbox" checked={lines[l.id]?.sealIntact ?? true} onChange={(e) => setLines((s) => ({ ...s, [l.id]: { ...s[l.id], sealIntact: e.target.checked } }))} />
                  {t("returns.sealIntact")}
                </label>
              )}
            </div>
          );
        })}
      </fieldset>

      <div className="field">
        <label htmlFor="reason">{t("returns.reason")}</label>
        <textarea id="reason" className="textarea" required maxLength={1000} value={reason} onChange={(e) => setReason(e.target.value)} />
      </div>

      {kind !== "withdrawal" && (
        <div className="field">
          <label htmlFor="photos">{t("returns.photos")}</label>
          <input id="photos" type="file" accept="image/jpeg,image/png" multiple onChange={(e) => void upload(e.target.files)} />
          {photos.length > 0 && <span className="muted small">{photos.length} ✓</span>}
        </div>
      )}

      {error && (
        <p className="field-error" role="alert">
          {error}
        </p>
      )}
      <button className="btn" type="submit" disabled={state === "sending"}>
        {t("returns.submit")}
      </button>
    </form>
  );
}
