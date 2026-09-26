"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useAction } from "@/components/admin/ui";
import { bff } from "@/lib/client/bff";
import { useLocale, useT } from "@/lib/client/i18n";

type Row = { id: string; slug: string; section: string; type: string; status: string; name: string; variants: number; isFeatured: boolean };

const TYPES = ["perfume", "attar", "hair_home_mist", "discovery_set", "honey", "comb_honey", "nuts_in_honey", "honey_blend", "gift_box", "bundle"];

export default function AdminProducts() {
  const t = useT();
  const locale = useLocale();
  const router = useRouter();
  const [q, setQ] = useState("");
  const [type, setType] = useState("honey");
  const [slug, setSlug] = useState("");
  const { run, busy, error } = useAction();
  const { data } = useQuery({ queryKey: ["admin", "products", q], queryFn: () => bff<Row[]>(`admin/products${q ? `?q=${encodeURIComponent(q)}` : ""}`) });

  return (
    <>
      <h1>{t("admin.products")}</h1>
      <form
        className="admin-toolbar"
        onSubmit={(e) => {
          e.preventDefault();
          void run(async () => {
            const id = await bff<string>("admin/products", { method: "POST", body: { type, slug } });
            router.push(`/${locale}/admin/products/${id}`);
          });
        }}
      >
        <strong>{t("admin.newProduct")}</strong>
        <select className="select" value={type} onChange={(e) => setType(e.target.value)} aria-label={t("admin.type")}>
          {TYPES.map((ty) => (
            <option key={ty} value={ty}>
              {ty}
            </option>
          ))}
        </select>
        <input className="input" required pattern="[a-z0-9]+(-[a-z0-9]+)*" placeholder={t("admin.slug")} value={slug} onChange={(e) => setSlug(e.target.value)} aria-label={t("admin.slug")} />
        <button className="btn" type="submit" disabled={busy}>
          {t("common.add")}
        </button>
      </form>
      {error}
      <input className="input" placeholder={t("admin.search")} value={q} onChange={(e) => setQ(e.target.value)} aria-label={t("admin.search")} />
      <table className="admin-table">
        <thead>
          <tr>
            <th>{t("admin.name")}</th>
            <th>{t("admin.type")}</th>
            <th>{t("admin.status")}</th>
            <th>{t("admin.variants")}</th>
          </tr>
        </thead>
        <tbody>
          {data?.map((p) => (
            <tr key={p.id}>
              <td>
                <Link href={`/${locale}/admin/products/${p.id}`}>{p.name}</Link> <span className="muted small">/{p.slug}</span>
              </td>
              <td>{p.type}</td>
              <td>
                <span className={`status status-${p.status}`}>{p.status}</span>
              </td>
              <td>{p.variants}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}
