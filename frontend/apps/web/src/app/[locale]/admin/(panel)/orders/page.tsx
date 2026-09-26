"use client";

import { formatDate } from "@rahiq/ui";
import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { useMoney } from "@/components/admin/ui";
import { bff } from "@/lib/client/bff";
import { useLocale, useT } from "@/lib/client/i18n";

type Row = { id: string; number: string; status: string; paymentMethod: string; total: number; email: string; city: string; placedAt: string; items: number; flagged: boolean; hasBothSections: boolean };

const STATUSES = ["confirmed", "preparing", "shipped", "pending_payment", "delivered", "return_requested", "cancelled"];

export default function AdminOrders() {
  const t = useT();
  const locale = useLocale();
  const money = useMoney();
  const [status, setStatus] = useState<string>("confirmed");
  const [q, setQ] = useState("");
  const { data, isLoading } = useQuery({
    queryKey: ["admin", "orders", status, q],
    queryFn: () => bff<Row[]>(`admin/orders?${new URLSearchParams({ ...(status ? { status } : {}), ...(q ? { q } : {}) })}`),
    refetchInterval: 30_000,
  });

  return (
    <>
      <h1>{t("admin.orders")}</h1>
      <div className="admin-toolbar">
        <div className="chips">
          <button type="button" className="chip" aria-pressed={status === ""} onClick={() => setStatus("")}>
            {t("admin.all")}
          </button>
          {STATUSES.map((s) => (
            <button key={s} type="button" className="chip" aria-pressed={status === s} onClick={() => setStatus(s)}>
              {t(`order.statuses.${s}`)}
            </button>
          ))}
        </div>
        <input className="input" placeholder={t("admin.searchOrders")} value={q} onChange={(e) => setQ(e.target.value)} aria-label={t("admin.search")} />
      </div>
      {isLoading && <p className="muted">{t("common.loading")}</p>}
      <table className="admin-table">
        <thead>
          <tr>
            <th>{t("order.number")}</th>
            <th>{t("order.placed")}</th>
            <th>{t("admin.email")}</th>
            <th>{t("checkout.province")}</th>
            <th>{t("order.items")}</th>
            <th>{t("common.total")}</th>
            <th>{t("admin.status")}</th>
          </tr>
        </thead>
        <tbody>
          {data?.map((o) => (
            <tr key={o.id}>
              <td>
                <Link href={`/${locale}/admin/orders/${o.id}`}>{o.number}</Link>
                {o.flagged && <span className="tag tag-warn"> !</span>}
                {o.hasBothSections && <span className="tag"> ◐</span>}
              </td>
              <td>{formatDate(o.placedAt, locale, true)}</td>
              <td>{o.email}</td>
              <td>{o.city}</td>
              <td>{o.items}</td>
              <td>
                {money(o.total)} {o.paymentMethod === "cod" ? "· COD" : ""}
              </td>
              <td>{t(`order.statuses.${o.status}`)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {data?.length === 0 && <p className="muted">{t("admin.none")}</p>}
    </>
  );
}
