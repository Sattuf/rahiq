"use client";

import { formatDate } from "@rahiq/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useAction } from "@/components/admin/ui";
import { bff } from "@/lib/client/bff";
import { useLocale, useT } from "@/lib/client/i18n";

type Staff = { id: string; email: string; name: string; role: string; isActive: boolean; totpEnabled: boolean; lastLoginAt?: string | null };
const ROLES = ["owner", "manager", "content", "fulfillment", "support"];

export default function AdminStaff() {
  const t = useT();
  const locale = useLocale();
  const client = useQueryClient();
  const { run, busy, error } = useAction();
  const { data } = useQuery({ queryKey: ["admin", "staff"], queryFn: () => bff<Staff[]>("admin/staff") });
  const refresh = () => client.invalidateQueries({ queryKey: ["admin", "staff"] });

  return (
    <>
      <h1>{t("admin.staff")}</h1>
      {error}
      <form
        className="panel form-grid"
        onSubmit={(e) => {
          e.preventDefault();
          const f = new FormData(e.currentTarget);
          void run(() => bff("admin/staff", { method: "POST", body: { email: f.get("email"), name: f.get("name"), role: f.get("role"), password: f.get("password") } }), () => void refresh());
        }}
      >
        <div className="field">
          <label htmlFor="email">{t("admin.email")}</label>
          <input id="email" name="email" type="email" className="input" required />
        </div>
        <div className="field">
          <label htmlFor="name">{t("admin.name")}</label>
          <input id="name" name="name" className="input" required />
        </div>
        <div className="field">
          <label htmlFor="role">{t("admin.role")}</label>
          <select id="role" name="role" className="select">
            {ROLES.map((r) => (
              <option key={r}>{r}</option>
            ))}
          </select>
        </div>
        <div className="field">
          <label htmlFor="password">{t("admin.password")}</label>
          <input id="password" name="password" type="password" className="input" minLength={10} required autoComplete="new-password" />
        </div>
        <button className="btn span-2" type="submit" disabled={busy}>
          {t("common.add")}
        </button>
      </form>
      <table className="admin-table">
        <thead>
          <tr>
            <th>{t("admin.name")}</th>
            <th>{t("admin.role")}</th>
            <th>2FA</th>
            <th>{t("admin.active")}</th>
          </tr>
        </thead>
        <tbody>
          {data?.map((s) => (
            <tr key={s.id}>
              <td>
                {s.name} <span className="muted small">{s.email}</span>
                {s.lastLoginAt && <span className="muted small"> · {formatDate(s.lastLoginAt, locale, true)}</span>}
              </td>
              <td>
                <select className="select" defaultValue={s.role} onChange={(e) => void run(() => bff(`admin/staff/${s.id}`, { method: "PUT", body: { role: e.target.value, isActive: s.isActive } }), () => void refresh())}>
                  {ROLES.map((r) => (
                    <option key={r}>{r}</option>
                  ))}
                </select>
              </td>
              <td>{s.totpEnabled ? "✓" : "—"}</td>
              <td>
                <input type="checkbox" defaultChecked={s.isActive} onChange={(e) => void run(() => bff(`admin/staff/${s.id}`, { method: "PUT", body: { role: s.role, isActive: e.target.checked } }), () => void refresh())} aria-label={t("admin.active")} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}
