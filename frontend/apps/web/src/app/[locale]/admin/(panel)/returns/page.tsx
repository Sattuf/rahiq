"use client";

import { formatDate } from "@rahiq/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { useAction, useMoney } from "@/components/admin/ui";
import { bff } from "@/lib/client/bff";
import { useLocale, useT } from "@/lib/client/i18n";

type ReturnRow = { id: string; orderId: string; orderNumber: string; status: string; kind: string; reason: string; photos: number; note?: string | null; refundAmount?: number | null; createdAt: string };

export default function AdminReturns() {
  const t = useT();
  const locale = useLocale();
  const money = useMoney();
  const client = useQueryClient();
  const { run, busy, error } = useAction();
  const [status, setStatus] = useState("requested");
  const { data } = useQuery({ queryKey: ["admin", "returns", status], queryFn: () => bff<ReturnRow[]>(`admin/orders/returns${status ? `?status=${status}` : ""}`) });
  const act = (id: string, decision: string, damaged = false) =>
    run(() => bff(`admin/orders/returns/${id}`, { method: "POST", body: { decision, note: decision === "reject" ? window.prompt(t("admin.reject")) ?? "" : null, damaged } }), () => void client.invalidateQueries({ queryKey: ["admin"] }));

  return (
    <>
      <h1>{t("admin.returns")}</h1>
      <div className="chips">
        {["requested", "approved", "received", "refunded", "rejected", ""].map((s) => (
          <button key={s} type="button" className="chip" aria-pressed={status === s} onClick={() => setStatus(s)}>
            {s || t("admin.all")}
          </button>
        ))}
      </div>
      {error}
      <table className="admin-table">
        <thead>
          <tr>
            <th>{t("order.number")}</th>
            <th>{t("admin.kind")}</th>
            <th>{t("returns.reason")}</th>
            <th>{t("admin.status")}</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {data?.map((r) => (
            <tr key={r.id}>
              <td>
                <Link href={`/${locale}/admin/orders/${r.orderId}`}>{r.orderNumber}</Link>
                <br />
                <span className="muted small">{formatDate(r.createdAt, locale)}</span>
              </td>
              <td>
                {t(`returns.${r.kind}`)} {r.photos > 0 && `· 📷 ${r.photos}`}
              </td>
              <td>{r.reason}</td>
              <td>
                {r.status} {r.refundAmount ? `· ${money(r.refundAmount)}` : ""}
              </td>
              <td className="row-actions">
                {r.status === "requested" && (
                  <>
                    <button className="btn" disabled={busy} onClick={() => act(r.id, "approve")}>
                      {t("admin.approve")}
                    </button>
                    <button className="btn btn-quiet" disabled={busy} onClick={() => act(r.id, "reject")}>
                      {t("admin.reject")}
                    </button>
                  </>
                )}
                {r.status === "approved" && (
                  <>
                    <button className="btn" disabled={busy} onClick={() => act(r.id, "receive")}>
                      {t("admin.receive")}
                    </button>
                    <button className="btn btn-quiet" disabled={busy} onClick={() => act(r.id, "receive", true)}>
                      {t("admin.damagedGoods")}
                    </button>
                  </>
                )}
                {r.status === "received" && (
                  <button className="btn btn-buy" disabled={busy} onClick={() => act(r.id, "refund")}>
                    {t("admin.refund")}
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}
