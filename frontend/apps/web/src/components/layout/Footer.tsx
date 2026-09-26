import { type Locale, translator } from "@rahiq/i18n";
import { Logo } from "@rahiq/ui";
import Link from "next/link";
import { config } from "@/lib/server/config";

/** Footer with the seller information e-commerce law requires on the site (ETBİS, compliance.md §3). */
export function Footer({ locale }: { locale: Locale }) {
  const t = translator(locale);
  const s = config.seller;
  return (
    <footer className="site-footer">
      <div className="container footer-grid">
        <div>
          <Logo locale={locale} />
          <p className="muted footer-lede">{t("home.tagline")}</p>
          <p className="muted small">{t("footer.payments")}</p>
        </div>
        <nav aria-label={t("footer.guides")}>
          <h2 className="footer-title">{t("footer.guides")}</h2>
          <Link href={`/${locale}/pages/bal-neden-kristallesir`}>{locale === "ar" ? "لماذا يتبلور العسل؟" : locale === "en" ? "Why honey crystallises" : "Bal neden kristalleşir?"}</Link>
          <Link href={`/${locale}/pages/extrait-edp-edt`}>Extrait · EDP · EDT</Link>
          <Link href={`/${locale}/guide`}>{t("nav.guide")}</Link>
          <Link href={`/${locale}/track`}>{t("nav.track")}</Link>
        </nav>
        <nav aria-label={t("footer.about")}>
          <h2 className="footer-title">{t("footer.about")}</h2>
          <Link href={`/${locale}/pages/hakkimizda`}>{t("footer.about")}</Link>
          <Link href={`/${locale}/pages/gizlilik`}>{t("footer.privacy")}</Link>
          <Link href={`/${locale}/pages/iade`}>{t("footer.returns")}</Link>
          <Link href={`/${locale}/pages/cerezler`}>{t("footer.cookies")}</Link>
        </nav>
        <address className="footer-seller">
          <h2 className="footer-title">{t("footer.seller")}</h2>
          <span>{s.name}</span>
          <span>{s.address}</span>
          <span>MERSİS {s.mersis} · ETBİS {s.etbis}</span>
          <a href={`mailto:${s.email}`}>{s.email}</a>
          <span>{s.phone}</span>
        </address>
      </div>
      <div className="container footer-bottom muted small">
        © {new Date().getFullYear()} Rahiq. {t("footer.rights")}
      </div>
    </footer>
  );
}
