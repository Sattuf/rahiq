"use client";

import { formatDate } from "@rahiq/ui";
import Link from "next/link";
import { useState } from "react";
import { useAction } from "@/components/admin/ui";
import { bff } from "@/lib/client/bff";
import { useLocale, useT } from "@/lib/client/i18n";

type Row = { id: string; number: string; status: string; email: string; city: string; placedAt: string };

/** If a batch has a problem, every order that received it, at once (operations.md §4). */
export default function AdminRecall() {
  const t = useT();
  const locale = useLocale();
  const { run, busy, error } = useAction();
  const [code, setCode] = useState("");
  const [rows, setRows] = useState<Row[] | null>(null);

  return (
    <>
      <h1>{t("admin.recall")}</h1>
      <form
        className="admin-toolbar"
        onSubmit={(e) => {
          e.preventDefault();
          void run(async () => setRows(await bff<Row[]>(`admin/orders/recall/${encodeURIComponent(code.trim())}`)));
        }}
      >
        <input className="input" required placeholder={t("admin.batchCode")} value={code} onChange={(e) => setCode(e.target.value)} aria-label={t("admin.batchCode")} />
        <button className="btn" type="submit" disabled={busy}>
          {t("admin.search")}
        </button>
      </form>
      {error}
      {rows && (
        <>
          <p className="muted">{rows.length}</p>
          <table className="admin-table">
            <tbody>
              {rows.map((r) => (
                <tr key={r.id}>
                  <td>
                    <Link href={`/${locale}/admin/orders/${r.id}`}>{r.number}</Link>
                  </td>
                  <td>{r.email}</td>
                  <td>{r.city}</td>
                  <td>{formatDate(r.placedAt, locale)}</td>
                  <td>{t(`order.statuses.${r.status}`)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <button
            type="button"
            className="btn"
            onClick={() => {
              const csv = ["number,email,city,placed,status", ...rows.map((r) => [r.number, r.email, r.city, r.placedAt, r.status].join(","))].join("\n");
              const a = document.createElement("a");
              a.href = URL.createObjectURL(new Blob([csv], { type: "text/csv" }));
              a.download = `recall-${code}.csv`;
              a.click();
            }}
          >
            CSV
          </button>
        </>
      )}
    </>
  );
}
