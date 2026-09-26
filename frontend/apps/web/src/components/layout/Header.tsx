import { type Locale, translator } from "@rahiq/i18n";
import { Logo } from "@rahiq/ui";
import Link from "next/link";
import { CartButton } from "./CartButton";
import { LanguageSwitcher } from "./LanguageSwitcher";
import { MobileMenu } from "./MobileMenu";

export function Header({ locale }: { locale: Locale }) {
  const t = translator(locale);
  const links = [
    { href: `/${locale}/perfume`, label: t("nav.perfume") },
    { href: `/${locale}/honey`, label: t("nav.honey") },
    { href: `/${locale}/gift-box`, label: t("nav.gifts") },
    { href: `/${locale}/guide`, label: t("nav.guide") },
  ];

  return (
    <header className="site-header">
      <a className="skip-link" href="#main">
        {t("nav.skip")}
      </a>
      <div className="container site-header-inner">
        <MobileMenu locale={locale} links={links} />
        <Link href={`/${locale}`} aria-label="Rahiq">
          <Logo locale={locale} />
        </Link>
        <nav aria-label={t("nav.menu")} className="site-nav">
          {links.map((l) => (
            <Link key={l.href} href={l.href}>
              {l.label}
            </Link>
          ))}
        </nav>
        <div className="site-tools">
          <Link className="btn btn-quiet" href={`/${locale}/search`} aria-label={t("nav.search")}>
            <SearchIcon />
          </Link>
          <Link className="btn btn-quiet hide-sm" href={`/${locale}/account`}>
            {t("nav.account")}
          </Link>
          <LanguageSwitcher locale={locale} />
          <CartButton />
        </div>
      </div>
    </header>
  );
}

function SearchIcon() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
      <circle cx="11" cy="11" r="7" />
      <path d="m20 20-3.5-3.5" />
    </svg>
  );
}
