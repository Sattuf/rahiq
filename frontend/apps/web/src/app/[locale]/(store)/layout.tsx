import type { Locale } from "@rahiq/i18n";
import { cookies } from "next/headers";
import type { ReactNode } from "react";
import { QueryProvider } from "@/components/layout/QueryProvider";
import { CartDrawer } from "@/components/layout/CartDrawer";
import { CookieBanner } from "@/components/layout/CookieBanner";
import { Footer } from "@/components/layout/Footer";
import { Header } from "@/components/layout/Header";
import { SmoothScroll } from "@/components/layout/SmoothScroll";

export default async function StoreLayout({ children, params }: { children: ReactNode; params: Promise<{ locale: string }> }) {
  const locale = (await params).locale as Locale;
  // Decided on the server so the banner is in the first HTML: rendered after hydration it became the LCP element.
  const undecided = !(await cookies()).has("rahiq_consent");
  return (
    <QueryProvider>
      <Header locale={locale} />
      <main id="main">{children}</main>
      <Footer locale={locale} />
      <CartDrawer />
      {undecided && <CookieBanner />}
      <SmoothScroll />
    </QueryProvider>
  );
}
