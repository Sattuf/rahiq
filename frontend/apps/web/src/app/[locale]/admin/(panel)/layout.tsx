import { type Locale, translator } from "@rahiq/i18n";
import { Logo } from "@rahiq/ui";
import type { Metadata } from "next";
import Link from "next/link";
import { redirect } from "next/navigation";
import type { ReactNode } from "react";
import { QueryProvider } from "@/components/layout/QueryProvider";
import { AdminSignOut } from "@/components/admin/AdminSignOut";
import { readSession } from "@/lib/server/session";

export const metadata: Metadata = { robots: { index: false, follow: false }, title: "Admin" };

/** The admin lives behind a staff session with a second factor; the API checks every permission again. */
export default async function AdminLayout({ children, params }: { children: ReactNode; params: Promise<{ locale: string }> }) {
  const locale = (await params).locale as Locale;
  const t = translator(locale);
  const session = await readSession();
  if (session?.kind !== "staff") redirect(`/${locale}/admin/login`);
  const can = (p: string) => session.permissions?.includes(p);
  const base = `/${locale}/admin`;

  const nav = [
    { href: base, label: t("admin.dashboard"), show: can("orders.view") },
    { href: `${base}/conversations`, label: t("admin.conversations"), show: can("conversations.view") },
    { href: `${base}/orders`, label: t("admin.orders"), show: can("orders.view") },
    { href: `${base}/returns`, label: t("admin.returns"), show: can("returns.process") },
    { href: `${base}/products`, label: t("admin.products"), show: can("products.edit") },
    { href: `${base}/inventory`, label: t("admin.inventory"), show: can("inventory.edit") },
    { href: `${base}/recall`, label: t("admin.recall"), show: can("orders.view") },
    { href: `${base}/coupons`, label: t("admin.coupons"), show: can("coupons.edit") },
    { href: `${base}/content`, label: t("admin.content"), show: can("content.edit") },
    { href: `${base}/staff`, label: t("admin.staff"), show: can("staff.manage") },
  ];

  return (
    <div className="admin">
      <aside className="admin-side">
        <Link href={base}>
          <Logo locale={locale} size={22} />
        </Link>
        <p className="muted small">{session.name ?? session.email}</p>
        <nav aria-label={t("admin.title")}>
          {nav.filter((n) => n.show).map((n) => (
            <Link key={n.href} href={n.href}>
              {n.label}
            </Link>
          ))}
        </nav>
        <AdminSignOut label={t("admin.signOut")} />
      </aside>
      <main id="main" className="admin-main">
        <QueryProvider>{children}</QueryProvider>
      </main>
    </div>
  );
}
