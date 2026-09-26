import { type Locale, translator } from "@rahiq/i18n";
import { Logo } from "@rahiq/ui";
import Link from "next/link";
import type { ReactNode } from "react";

/**
 * Checkout layout (Law 10): logo, the steps, a fixed summary, trust signals, big buttons. No three.js, no shaders,
 * no GSAP, no smooth scroll here (testing.md §4 item 7 checks the bundle).
 */
export default async function CheckoutLayout({ children, params }: { children: ReactNode; params: Promise<{ locale: string }> }) {
  const locale = (await params).locale as Locale;
  const t = translator(locale);
  return (
    <>
      <header className="checkout-header">
        <div className="container checkout-header-inner">
          <Link href={`/${locale}`} aria-label="Rahiq">
            <Logo locale={locale} />
          </Link>
          <span className="secure-note">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
              <rect x="5" y="11" width="14" height="10" rx="2" />
              <path d="M8 11V8a4 4 0 0 1 8 0v3" />
            </svg>
            {t("checkout.secure")}
          </span>
        </div>
      </header>
      <main id="main" className="checkout-main">
        {children}
      </main>
      <noscript>
        <p className="container notice">{t("checkout.jsRequired")}</p>
      </noscript>
    </>
  );
}
