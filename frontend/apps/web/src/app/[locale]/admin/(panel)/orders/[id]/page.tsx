"use client";

import type { OrderView } from "@rahiq/api-client";
import { formatDate } from "@rahiq/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { use, useState } from "react";
import { OrderDetail } from "@/components/account/OrderDetail";
import { toMinor, useAction, useMoney } from "@/components/admin/ui";
import { bff } from "@/lib/client/bff";
import { useLocale, useT } from "@/lib/client/i18n";

type Shipment = { id: string; carrier: string; trackingNumber?: string | null; status: string; hasLabel: boolean; hasPackagePhoto: boolean; events: { at: string; status: string }[] };

async function openHtml(path: string) {
  const html = await bff<string>(path);
  const win = window.open("", "_blank");
  if (win) {
    win.document.open();
    win.document.write(html);
    win.document.close();
    win.focus();
  }
}

export default function AdminOrder({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const t = useT();
  const locale = useLocale();
  const money = useMoney();
  const client = useQueryClient();
  const { run, busy, error } = useAction();
  const order = useQuery({ queryKey: ["admin", "order", id], queryFn: () => bff<OrderView>(`admin/orders/${id}`, { locale }) });
  const shipments = useQuery({ queryKey: ["admin", "shipments", id], queryFn: () => bff<Shipment[]>(`admin/shipping/orders/${id}`) });
  const notes = useQuery({ queryKey: ["admin", "notes", id], queryFn: () => bff<{ body: string; at: string }[]>(`admin/orders/${id}/notes`) });
  const [note, setNote] = useState("");
  const [refund, setRefund] = useState({ amount: "", reason: "" });

  const refresh = () => {
    void client.invalidateQueries({ queryKey: ["admin"] });
  };

  const o = order.data;
  if (!o) return <p className="muted">{t("common.loading")}</p>;
  const shipment = shipments.data?.find((s) => s.status !== "failed");

  // One obvious next action per status (Phase 9 GATE: a non-technical person can do it unaided).
  const next = (() => {
    if (o.status === "confirmed")
      return (
        <button className="btn btn-buy" disabled={busy} onClick={() => run(() => bff(`admin/orders/${id}/prepare`, { method: "POST" }), refresh)}>
          {t("admin.prepare")}
        </button>
      );
    if (o.status === "preparing" && !shipment)
      return (
        <>
          <button className="btn" onClick={() => void openHtml(`admin/orders/${id}/pick-list`)}>
            1. {t("admin.pickList")}
          </button>
          <button className="btn btn-buy" disabled={busy} onClick={() => run(() => bff(`admin/shipping/orders/${id}`, { method: "POST" }), refresh)}>
            2. {t("admin.createLabel")}
          </button>
        </>
      );
    if (o.status === "preparing" && shipment)
      return (
        <>
          <button className="btn" onClick={() => void openHtml(`admin/shipping/${shipment.id}/label`)}>
            3. {t("admin.printLabel")}
          </button>
          <label className="btn">
            4. {t("admin.parcelPhoto")} {shipment.hasPackagePhoto ? "✓" : ""}
            <input
              type="file"
              accept="image/jpeg,image/png"
              capture="environment"
              hidden
              onChange={(e) => {
                const file = e.target.files?.[0];
                if (!file) return;
                const form = new FormData();
                form.append("file", file);
                void run(() => bff(`admin/shipping/${shipment.id}/photo`, { method: "POST", form }), refresh);
              }}
            />
          </label>
          <button className="btn btn-buy" disabled={busy} onClick={() => run(() => bff(`admin/shipping/${shipment.id}/handover`, { method: "POST" }), () => setTimeout(refresh, 1500))}>
            5. {t("admin.handover")}
          </button>
        </>
      );
    if (o.status === "shipped")
      return (
        <button className="btn" disabled={busy} onClick={() => run(() => bff(`admin/orders/${id}/delivered`, { method: "POST" }), refresh)}>
          {t("admin.markDelivered")}
        </button>
      );
    return null;
  })();

  return (
    <>
      <div className="admin-actions">{next}</div>
      {error}
      {o.fraudFlags.length > 0 && <p className="notice notice-danger">Review: {o.fraudFlags.join(", ")}</p>}
      <div className="admin-two">
        <OrderDetail order={o} />
        <div className="stack">
          <p className="muted small">
            {o.email} · {o.phone}
          </p>
          {shipment && (
            <section className="panel">
              <h2 className="h-small">{shipment.carrier} · {shipment.trackingNumber}</h2>
              <ul className="small">
                {shipment.events.map((e, i) => (
                  <li key={i}>
                    {e.status} · {formatDate(e.at, locale, true)}
                  </li>
                ))}
              </ul>
            </section>
          )}

          <section className="panel">
            <h2 className="h-small">{t("admin.notes")}</h2>
            <ul className="small">
              {notes.data?.map((n, i) => (
                <li key={i}>
                  {n.body} <span className="muted">{formatDate(n.at, locale, true)}</span>
                </li>
              ))}
            </ul>
            <form
              onSubmit={(e) => {
                e.preventDefault();
                void run(() => bff(`admin/orders/${id}/notes`, { method: "POST", body: { body: note } }), () => {
                  setNote("");
                  refresh();
                });
              }}
            >
              <textarea className="textarea" value={note} onChange={(e) => setNote(e.target.value)} aria-label={t("admin.addNote")} />
              <button className="btn" type="submit" disabled={!note.trim()}>
                {t("admin.addNote")}
              </button>
            </form>
          </section>

          {["confirmed", "delivered", "returned", "returned_to_sender", "cancelled"].includes(o.status) && o.paymentStatus && o.paymentStatus !== "pending" && (
            <section className="panel">
              <h2 className="h-small">{t("admin.refund")}</h2>
              <div className="field">
                <label htmlFor="refund-amount">{t("admin.refundAmount")}</label>
                <input id="refund-amount" className="input" inputMode="decimal" value={refund.amount} onChange={(e) => setRefund({ ...refund, amount: e.target.value })} placeholder={money(o.total)} />
              </div>
              <div className="field">
                <label htmlFor="refund-reason">{t("admin.refundReason")}</label>
                <input id="refund-reason" className="input" value={refund.reason} onChange={(e) => setRefund({ ...refund, reason: e.target.value })} />
              </div>
              <button
                className="btn"
                disabled={busy || !refund.amount || !refund.reason}
                onClick={() => run(() => bff(`admin/orders/${id}/refund`, { method: "POST", body: { amount: toMinor(refund.amount), reason: refund.reason } }), refresh)}
              >
                {t("admin.refund")}
              </button>
            </section>
          )}

          {["pending_payment", "confirmed"].includes(o.status) && (
            <button
              className="btn btn-danger"
              disabled={busy}
              onClick={() => {
                if (window.confirm(t("admin.cancelOrder"))) void run(() => bff(`admin/orders/${id}/cancel`, { method: "POST", body: { note: "staff" } }), refresh);
              }}
            >
              {t("admin.cancelOrder")}
            </button>
          )}
        </div>
      </div>
    </>
  );
}
