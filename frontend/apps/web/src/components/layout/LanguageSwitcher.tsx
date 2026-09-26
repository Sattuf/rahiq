"use client";

import type { Locale } from "@rahiq/i18n/core";
import { usePathname, useSearchParams } from "next/navigation";
import { useT } from "@/lib/client/i18n";

const NAMES: Record<Locale, string> = { tr: "Türkçe", ar: "العربية", en: "English" };

export function LanguageSwitcher({ locale }: { locale: Locale }) {
  const t = useT();
  const pathname = usePathname();
  const search = useSearchParams();

  const hrefFor = (target: Locale) => {
    const rest = pathname.split("/").slice(2).join("/");
    const query = search.toString();
    return `/${target}${rest ? `/${rest}` : ""}${query ? `?${query}` : ""}`;
  };

  return (
    <label className="lang-switch">
      <span className="sr-only">{t("nav.language")}</span>
      <select
        className="select lang-select"
        value={locale}
        onChange={(e) => {
          const target = e.target.value as Locale;
          document.cookie = `rahiq_locale=${target}; path=/; max-age=31536000; samesite=lax`;
          window.location.assign(hrefFor(target));
        }}
      >
        {(Object.keys(NAMES) as Locale[]).map((l) => (
          <option key={l} value={l} lang={l}>
            {NAMES[l]}
          </option>
        ))}
      </select>
    </label>
  );
}
