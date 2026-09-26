"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import { toMinor, useAction, useMoney } from "@/components/admin/ui";
import { bff } from "@/lib/client/bff";
import { useT } from "@/lib/client/i18n";

type Coupon = { id: string; code: string; kind: string; value: number; minSubtotal?: number | null; section?: string | null; maxUses?: number | null; usedCount: number; perCustomerLimit?: number | null; active: boolean; description?: string | null };

/** One coupon per order in V1; usage is counted at pay, atomically (ADR-016). */
export default function AdminCoupons() {
  const t = useT();
  const money = useMoney();
  const client = useQueryClient();
  const { run, busy, error } = useAction();
  const { data } = useQuery({ queryKey: ["admin", "coupons"], queryFn: () => bff<Coupon[]>("admin/pricing/coupons") });

  return (
    <>
      <h1>{t("admin.coupons")}</h1>
      {error}
      <form
        className="form-grid panel"
        onSubmit={(e) => {
          e.preventDefault();
          const f = new FormData(e.currentTarget);
          const kind = String(f.get("kind"));
          const raw = String(f.get("value") || "0");
          const value = kind === "percent" ? Math.round(Number(raw) * 100) : kind === "fixed" ? toMinor(raw) : 0;
          const int = (k: string) => (f.get(k) ? Number(f.get(k)) : null);
          void run(
            () =>
              bff("admin/pricing/coupons", {
                method: "POST",
                body: {
                  code: f.get("code"), kind, value, minSubtotal: f.get("minSubtotal") ? toMinor(String(f.get("minSubtotal"))) : null, section: f.get("section") || null,
                  maxUses: int("maxUses"), perCustomerLimit: int("perCustomer"), startsAt: f.get("startsAt") || null, endsAt: f.get("endsAt") || null,
                  description: f.get("description") || null, active: true,
                },
              }),
            () => void client.invalidateQueries({ queryKey: ["admin", "coupons"] }),
          );
        }}
      >
        <div className="field">
          <label htmlFor="code">{t("admin.code")}</label>
          <input id="code" name="code" className="input" required pattern="[A-Za-z0-9-]{3,32}" />
        </div>
        <div className="field">
          <label htmlFor="kind">{t("admin.kind")}</label>
          <select id="kind" name="kind" className="select">
            <option value="percent">%</option>
            <option value="fixed">₺</option>
            <option value="free_shipping">free shipping</option>
          </select>
        </div>
        <div className="field">
          <label htmlFor="value">{t("admin.value")}</label>
          <input id="value" name="value" className="input" inputMode="decimal" />
        </div>
        <div className="field">
          <label htmlFor="minSubtotal">{t("admin.minSubtotal")}</label>
          <input id="minSubtotal" name="minSubtotal" className="input" inputMode="decimal" />
        </div>
        <div className="field">
          <label htmlFor="section">{t("admin.section")}</label>
          <select id="section" name="section" className="select">
            <option value="">{t("admin.all")}</option>
            <option value="perfume">perfume</option>
            <option value="honey">honey</option>
          </select>
        </div>
        <div className="field">
          <label htmlFor="maxUses">{t("admin.maxUses")}</label>
          <input id="maxUses" name="maxUses" className="input" type="number" min={1} />
        </div>
        <div className="field">
          <label htmlFor="perCustomer">{t("admin.perCustomer")}</label>
          <input id="perCustomer" name="perCustomer" className="input" type="number" min={1} />
        </div>
        <div className="field">
          <label htmlFor="startsAt">start</label>
          <input id="startsAt" name="startsAt" className="input" type="datetime-local" />
        </div>
        <div className="field">
          <label htmlFor="endsAt">end</label>
          <input id="endsAt" name="endsAt" className="input" type="datetime-local" />
        </div>
        <div className="field span-2">
          <label htmlFor="description">description</label>
          <input id="description" name="description" className="input" />
        </div>
        <button className="btn btn-buy span-2" type="submit" disabled={busy}>
          {t("common.add")}
        </button>
      </form>

      <table className="admin-table">
        <thead>
          <tr>
            <th>{t("admin.code")}</th>
            <th>{t("admin.value")}</th>
            <th>{t("admin.maxUses")}</th>
            <th>{t("admin.active")}</th>
          </tr>
        </thead>
        <tbody>
          {data?.map((c) => (
            <tr key={c.id}>
              <td>
                <strong>{c.code}</strong> <span className="muted small">{c.description}</span>
              </td>
              <td>
                {c.kind === "percent" ? `%${c.value / 100}` : c.kind === "fixed" ? money(c.value) : "free shipping"}
                {c.section ? ` · ${c.section}` : ""} {c.minSubtotal ? `· ≥ ${money(c.minSubtotal)}` : ""}
              </td>
              <td>
                {c.usedCount}/{c.maxUses ?? "∞"}
              </td>
              <td>{c.active ? "✓" : "—"}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}
