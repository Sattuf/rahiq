"use client";

import { formatDate } from "@rahiq/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import QRCode from "qrcode";
import { useState } from "react";
import { useAction } from "@/components/admin/ui";
import { bff } from "@/lib/client/bff";
import { useLocale, useT } from "@/lib/client/i18n";

type VariantRow = { variantId: string; sku: string; productName: string; label: string; section: string; isSample: boolean };
type StockRow = { variantId: string; onHand: number; reserved: number; free: number; threshold: number; nextExpiry?: string | null; batches: number };
type BatchRow = { id: string; variantId: string; code: string; qtyReceived: number; qtyOnHand: number; qtyReserved: number; producedAt?: string | null; bestBefore?: string | null; hasLabReport: boolean; publicToken: string };

const escapeHtml = (value: string) => value.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);

/**
 * Printable jar label with the QR that opens the batch page (brand-identity.md §9). Honey labels carry the infant
 * warning in Turkish (taxonomy.json `honey.infant-under-12-months`). The print window inherits this page's CSP, so it
 * gets no script of its own: printing is triggered from here.
 */
async function printLabel(batch: BatchRow, variant: VariantRow | undefined, locale: string) {
  const url = `${window.location.origin}/${locale}/batch/${batch.publicToken}`;
  const qr = await QRCode.toDataURL(url, { margin: 0, width: 240, errorCorrectionLevel: "M" });
  const win = window.open("", "_blank");
  if (!win) return;
  const name = escapeHtml(`${variant?.productName ?? ""} ${variant?.label ?? ""}`);
  const warning = variant?.section === "honey" ? "<br><small>1 yaşından küçük bebeklere verilmemelidir.</small>" : "";
  const label = `<div class="l"><img src="${qr}" alt=""><div><b>RAHIQ</b><br>${name}<br>Parti ${escapeHtml(batch.code)}<br>TETT ${batch.bestBefore ?? "—"}${warning}</div></div>`;
  win.document.write(`<!doctype html><meta charset="utf-8"><title>${escapeHtml(batch.code)}</title><style>body{font:11px system-ui;margin:8mm}.l{display:inline-flex;gap:3mm;align-items:center;width:62mm;height:28mm;border:1px dashed #999;padding:2mm;margin:1mm;box-sizing:border-box}img{width:18mm;height:18mm}small{font-size:8px}</style>${label.repeat(Math.min(batch.qtyOnHand, 40))}`);
  win.document.close();
  let printed = false;
  const print = () => {
    if (printed) return;
    printed = true;
    win.focus();
    win.print();
  };
  win.addEventListener("load", print, { once: true });
  if (win.document.readyState === "complete") setTimeout(print, 100);
}

export default function AdminInventory() {
  const t = useT();
  const locale = useLocale();
  const client = useQueryClient();
  const { run, busy, error } = useAction();
  const variants = useQuery({ queryKey: ["admin", "variants"], queryFn: () => bff<VariantRow[]>("admin/variants") });
  const stock = useQuery({ queryKey: ["admin", "stock"], queryFn: () => bff<StockRow[]>("admin/inventory/stock") });
  const [variantId, setVariantId] = useState("");
  const batches = useQuery({ queryKey: ["admin", "batches", variantId], queryFn: () => bff<BatchRow[]>(`admin/inventory/batches${variantId ? `?variantId=${variantId}` : ""}`) });
  const byId = new Map(variants.data?.map((v) => [v.variantId, v]));
  const refresh = () => client.invalidateQueries({ queryKey: ["admin"] });

  return (
    <>
      <h1>{t("admin.inventory")}</h1>
      {error}

      <section className="panel">
        <h2 className="h-small">{t("admin.stock")}</h2>
        <table className="admin-table">
          <thead>
            <tr>
              <th>SKU</th>
              <th>{t("admin.name")}</th>
              <th>{t("admin.onHand")}</th>
              <th>{t("admin.reserved")}</th>
              <th>{t("admin.free")}</th>
              <th>{t("admin.nextExpiry")}</th>
            </tr>
          </thead>
          <tbody>
            {stock.data?.map((s) => {
              const v = byId.get(s.variantId);
              return (
                <tr key={s.variantId} className={s.free <= s.threshold ? "row-warn" : undefined}>
                  <td>
                    <button type="button" className="link-button" onClick={() => setVariantId(s.variantId)}>
                      {v?.sku ?? s.variantId.slice(0, 8)}
                    </button>
                  </td>
                  <td>
                    {v?.productName} {v?.label}
                  </td>
                  <td>{s.onHand}</td>
                  <td>{s.reserved}</td>
                  <td>{s.free}</td>
                  <td>{s.nextExpiry ? formatDate(s.nextExpiry, locale) : "—"}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </section>

      <section className="panel">
        <h2 className="h-small">{t("admin.receiveBatch")}</h2>
        <form
          className="form-grid"
          onSubmit={(e) => {
            e.preventDefault();
            const f = new FormData(e.currentTarget);
            const num = (k: string) => (f.get(k) ? Number(f.get(k)) : undefined);
            const origin = { region: f.get("region") || undefined, altitude: num("altitude"), season: f.get("season") || undefined, beekeeper: f.get("beekeeper") || undefined };
            const pollenName = f.get("pollenName");
            const lab = { moisture: num("moisture"), hmf: num("hmf"), diastase: num("diastase"), pollen: pollenName ? { name: pollenName, percent: num("pollenPercent") } : undefined, lab: f.get("lab") || undefined, reportDate: f.get("reportDate") || undefined };
            const hasLab = Object.values(lab).some((x) => x !== undefined);
            void run(
              () =>
                bff("admin/inventory/batches", {
                  method: "POST",
                  body: {
                    variantId: f.get("variantId"),
                    code: f.get("code"),
                    qty: Number(f.get("qty")),
                    producedAt: f.get("producedAt") || null,
                    bestBefore: f.get("bestBefore") || null,
                    origin: Object.values(origin).some(Boolean) ? origin : null,
                    labSummary: hasLab ? lab : null,
                  },
                }),
              () => {
                (e.target as HTMLFormElement).reset();
                void refresh();
              },
            );
          }}
        >
          <div className="field span-2">
            <label htmlFor="variantId">SKU</label>
            <select id="variantId" name="variantId" className="select" required defaultValue="">
              <option value="" disabled>
                —
              </option>
              {variants.data?.filter((v) => v.section !== "shared" || v.sku.includes("BOX")).map((v) => (
                <option key={v.variantId} value={v.variantId}>
                  {v.sku} · {v.productName} {v.label}
                </option>
              ))}
            </select>
          </div>
          {[
            ["code", t("admin.batchCode"), "text", true],
            ["qty", t("admin.quantity"), "number", true],
            ["producedAt", t("admin.producedAt"), "date", false],
            ["bestBefore", t("admin.bestBefore"), "date", false],
            ["region", t("product.origin"), "text", false],
            ["altitude", t("product.altitude"), "number", false],
            ["season", t("batch.season"), "text", false],
            ["beekeeper", t("batch.beekeeper"), "text", false],
            ["moisture", "Nem %", "number", false],
            ["hmf", "HMF mg/kg", "number", false],
            ["diastase", "Diastaz", "number", false],
            ["pollenName", "Polen", "text", false],
            ["pollenPercent", "Polen %", "number", false],
            ["lab", "Lab", "text", false],
            ["reportDate", "Rapor tarihi", "date", false],
          ].map(([name, label, type, required]) => (
            <div key={String(name)} className="field">
              <label htmlFor={String(name)}>{label}</label>
              <input id={String(name)} name={String(name)} className="input" type={String(type)} step="any" required={Boolean(required)} />
            </div>
          ))}
          <button className="btn btn-buy span-2" type="submit" disabled={busy}>
            {t("admin.receiveBatch")}
          </button>
        </form>
      </section>

      <section className="panel">
        <h2 className="h-small">
          {t("admin.batchCode")} {variantId && `· ${byId.get(variantId)?.sku}`}
        </h2>
        <table className="admin-table">
          <thead>
            <tr>
              <th>{t("admin.batchCode")}</th>
              <th>SKU</th>
              <th>{t("admin.onHand")}</th>
              <th>{t("admin.reserved")}</th>
              <th>{t("admin.bestBefore")}</th>
              <th>{t("admin.labReport")}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {batches.data?.map((b) => (
              <tr key={b.id}>
                <td>{b.code}</td>
                <td>{byId.get(b.variantId)?.sku}</td>
                <td>
                  {b.qtyOnHand}/{b.qtyReceived}
                </td>
                <td>{b.qtyReserved}</td>
                <td>{b.bestBefore ? formatDate(b.bestBefore, locale) : "—"}</td>
                <td>
                  {b.hasLabReport ? (
                    "✓"
                  ) : (
                    <label className="btn btn-quiet">
                      PDF
                      <input
                        type="file"
                        accept="application/pdf"
                        hidden
                        onChange={(e) => {
                          const file = e.target.files?.[0];
                          if (!file) return;
                          const form = new FormData();
                          form.append("file", file);
                          void run(() => bff(`admin/inventory/batches/${b.id}/lab-report`, { method: "POST", form }), () => void refresh());
                        }}
                      />
                    </label>
                  )}
                </td>
                <td className="row-actions">
                  <button type="button" className="btn btn-quiet" onClick={() => void printLabel(b, byId.get(b.variantId), locale)}>
                    {t("admin.printLabel")}
                  </button>
                  <button
                    type="button"
                    className="btn btn-quiet"
                    onClick={() => {
                      const value = window.prompt(`${t("admin.adjust")}: ${t("admin.onHand")}`, String(b.qtyOnHand));
                      if (value === null) return;
                      const note = window.prompt(t("admin.adjustNote")) ?? "";
                      void run(() => bff(`admin/inventory/batches/${b.id}/adjust`, { method: "POST", body: { newOnHand: Number(value), note } }), () => void refresh());
                    }}
                  >
                    {t("admin.adjust")}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </>
  );
}
