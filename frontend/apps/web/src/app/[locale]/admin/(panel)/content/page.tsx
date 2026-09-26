"use client";

import type { Page } from "@rahiq/api-client";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useAction } from "@/components/admin/ui";
import { bff } from "@/lib/client/bff";
import { useT } from "@/lib/client/i18n";

type Saved = { id: string; findings: { term: string; field: string; severity: number | string }[] };

export default function AdminContent() {
  const t = useT();
  const client = useQueryClient();
  const { run, busy, error } = useAction();
  const { data } = useQuery({ queryKey: ["admin", "pages"], queryFn: () => bff<Page[]>("admin/content/pages") });
  const [editing, setEditing] = useState<Partial<Page> | null>(null);
  const [findings, setFindings] = useState<Saved["findings"]>([]);
  const refresh = () => client.invalidateQueries({ queryKey: ["admin", "pages"] });

  return (
    <>
      <h1>{t("admin.content")}</h1>
      {error}
      <button className="btn" type="button" onClick={() => setEditing({ locale: "tr", kind: "guide", slug: "", title: "", bodyMarkdown: "" })}>
        {t("common.add")}
      </button>
      {editing && (
        <form
          className="panel form-grid"
          onSubmit={(e) => {
            e.preventDefault();
            void run(async () => {
              const saved = await bff<Saved>(editing.id ? `admin/content/pages/${editing.id}` : "admin/content/pages", {
                method: editing.id ? "PUT" : "POST",
                body: { slug: editing.slug, locale: editing.locale, kind: editing.kind, title: editing.title, summary: editing.summary ?? null, bodyMarkdown: editing.bodyMarkdown, relatedProductIds: [] },
              });
              setFindings(saved.findings);
              setEditing({ ...editing, id: saved.id });
              await refresh();
            });
          }}
        >
          <div className="field">
            <label htmlFor="slug">{t("admin.slug")}</label>
            <input id="slug" className="input" value={editing.slug ?? ""} onChange={(e) => setEditing({ ...editing, slug: e.target.value })} disabled={!!editing.id} />
          </div>
          <div className="field">
            <label htmlFor="locale">locale</label>
            <select id="locale" className="select" value={editing.locale} onChange={(e) => setEditing({ ...editing, locale: e.target.value })} disabled={!!editing.id}>
              <option>tr</option>
              <option>ar</option>
              <option>en</option>
            </select>
          </div>
          <div className="field">
            <label htmlFor="kind">{t("admin.kind")}</label>
            <select id="kind" className="select" value={editing.kind} onChange={(e) => setEditing({ ...editing, kind: e.target.value })} disabled={!!editing.id}>
              <option value="guide">guide</option>
              <option value="story">story</option>
              <option value="legal">{t("admin.kindLegal")}</option>
              <option value="faq">faq</option>
            </select>
          </div>
          <div className="field span-2">
            <label htmlFor="title">{t("admin.title2")}</label>
            <input id="title" className="input" value={editing.title ?? ""} onChange={(e) => setEditing({ ...editing, title: e.target.value })} />
          </div>
          <div className="field span-2">
            <label htmlFor="body">{t("admin.body")}</label>
            <textarea id="body" className="textarea" rows={14} dir={editing.locale === "ar" ? "rtl" : "ltr"} value={editing.bodyMarkdown ?? ""} onChange={(e) => setEditing({ ...editing, bodyMarkdown: e.target.value })} />
          </div>
          {findings.length > 0 && (
            <ul className="span-2 findings">
              {findings.map((f, i) => (
                <li key={i} className={String(f.severity) === "0" || f.severity === "Blocking" ? "field-error" : "muted"}>
                  {f.term} · {f.field}
                </li>
              ))}
            </ul>
          )}
          <div className="admin-actions span-2">
            <button className="btn" type="submit" disabled={busy}>
              {t("admin.save")}
            </button>
            {editing.id && (
              <button className="btn btn-buy" type="button" disabled={busy} onClick={() => run(() => bff(`admin/content/pages/${editing.id}/publish`, { method: "POST", body: { publish: true, approveClaims: false } }), () => void refresh())}>
                {t("admin.publish")}
              </button>
            )}
          </div>
        </form>
      )}
      <table className="admin-table">
        <thead>
          <tr>
            <th>{t("admin.title2")}</th>
            <th>slug</th>
            <th>locale</th>
            <th>{t("admin.status")}</th>
          </tr>
        </thead>
        <tbody>
          {data?.map((p) => (
            <tr key={p.id}>
              <td>
                <button type="button" className="link-button" onClick={() => { setEditing(p); setFindings([]); }}>
                  {p.title}
                </button>
              </td>
              <td>{p.slug}</td>
              <td>{p.locale}</td>
              <td>{p.status}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}
