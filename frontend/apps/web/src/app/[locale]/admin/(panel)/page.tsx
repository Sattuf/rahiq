"use client";

import { useQuery } from "@tanstack/react-query";
import { useMoney } from "@/components/admin/ui";
import { bff } from "@/lib/client/bff";
import { useT } from "@/lib/client/i18n";

type Dashboard = { ordersToday: number; toPrepare: number; pendingPayment: number; revenueLast7Days: number; ordersLast30Days: number; crossSectionShareBp: number; openReturns: number; flagged: number };

export default function AdminDashboard() {
  const t = useT();
  const money = useMoney();
  const { data } = useQuery({ queryKey: ["admin", "dashboard"], queryFn: () => bff<Dashboard>("admin/orders/dashboard"), refetchInterval: 60_000 });
  const tiles = data
    ? [
        [t("admin.ordersToday"), data.ordersToday],
        [t("admin.toPrepare"), data.toPrepare],
        [t("admin.pendingPayment"), data.pendingPayment],
        [t("admin.revenue7"), money(data.revenueLast7Days)],
        [t("admin.crossShare"), `%${(data.crossSectionShareBp / 100).toFixed(1)}`],
        [t("admin.openReturns"), data.openReturns],
        [t("admin.flagged"), data.flagged],
      ]
    : [];

  return (
    <>
      <h1>{t("admin.dashboard")}</h1>
      <div className="stat-grid">
        {tiles.map(([label, value]) => (
          <div key={String(label)} className="stat">
            <span className="muted small">{label}</span>
            <strong>{value}</strong>
          </div>
        ))}
      </div>
    </>
  );
}
