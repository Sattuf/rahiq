import { translator } from "@rahiq/i18n";
import { headers } from "next/headers";
import Link from "next/link";

export default async function NotFound() {
  const raw = (await headers()).get("x-locale");
  const locale = raw === "ar" || raw === "en" ? raw : "tr";
  const t = translator(locale);
  return (
    <main id="main" className="container narrow section">
      <h1>{t("errors.notFoundTitle")}</h1>
      <p className="lede">{t("errors.notFoundText")}</p>
      <p>
        <Link className="btn" href={`/${locale}`}>
          {t("nav.home")}
        </Link>
      </p>
    </main>
  );
}
