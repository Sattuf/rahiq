"use client";

import { useState } from "react";
import { useT } from "@/lib/client/i18n";

/**
 * KVKK cookie consent that actually works (testing.md launch list): no analytics loads before "allow".
 * The choice is stored in a first-party cookie; analytics, when added, must read `rahiq_consent=analytics`.
 * The store layout renders this only while that cookie is absent, so it is server-rendered and paints with the page.
 */
export function CookieBanner() {
  const t = useT();
  const [dismissed, setDismissed] = useState(false);

  const choose = (value: "analytics" | "essential") => {
    document.cookie = `rahiq_consent=${value}; path=/; max-age=${60 * 60 * 24 * 180}; samesite=lax`;
    setDismissed(true);
  };

  if (dismissed) return null;
  return (
    <div className="cookie-banner" role="region" aria-label="Cookies">
      <p>{t("cookie.text")}</p>
      <div className="cookie-actions">
        <button type="button" className="btn" onClick={() => choose("essential")}>
          {t("cookie.reject")}
        </button>
        <button type="button" className="btn" onClick={() => choose("analytics")}>
          {t("cookie.accept")}
        </button>
      </div>
    </div>
  );
}
