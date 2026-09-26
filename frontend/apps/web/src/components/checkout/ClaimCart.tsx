"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { useState, useSyncExternalStore } from "react";
import { bff } from "@/lib/client/bff";
import { useErrorText, useLocale, useT } from "@/lib/client/i18n";
import { ApiError } from "@rahiq/api-client";

/**
 * Takes over a cart prepared in a chat. The code is in the URL fragment, so it never reaches a server log; the claim is
 * a POST with the CSRF header (a page load alone never swaps someone's cart). The BFF stores the new cart cookie.
 */
export function ClaimCart() {
  const t = useT();
  const locale = useLocale();
  const errorText = useErrorText();
  const router = useRouter();
  const client = useQueryClient();
  // null while rendering on the server, "" when the link has no code.
  const code = useSyncExternalStore(noSubscription, () => window.location.hash.slice(1), () => null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const claim = async () => {
    if (!code) return;
    setBusy(true);
    setError(null);
    try {
      await bff("cart/claim", { method: "POST", body: { code }, locale });
      await client.invalidateQueries({ queryKey: ["cart"] });
      router.replace(`/${locale}/checkout`); // Replace: the spent link does not stay in the back history.
    } catch (e) {
      setError(errorText(e instanceof ApiError ? e.code : undefined));
      setBusy(false);
    }
  };

  return (
    <div className="container claim">
      <h1>{t("claim.title")}</h1>
      {code === "" ? (
        <p className="notice notice-danger">{t("claim.missing")}</p>
      ) : (
        <>
          <p>{t("claim.text")}</p>
          {error && (
            <p className="notice notice-danger" role="alert">
              {error}
            </p>
          )}
          <button type="button" className="btn btn-buy" disabled={busy || !code} onClick={claim}>
            {t("claim.button")}
          </button>
        </>
      )}
    </div>
  );
}

const noSubscription = () => () => {};
