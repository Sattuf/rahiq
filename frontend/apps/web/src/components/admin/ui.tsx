"use client";

import { ApiError } from "@rahiq/api-client";
import { formatMoney } from "@rahiq/ui";
import { useState } from "react";
import { useErrorText, useLocale } from "@/lib/client/i18n";

/** Small admin helpers: money in ₺ from kuruş and back, and a runner that shows API errors in plain language. */
export function useMoney() {
  const locale = useLocale();
  return (minor: number | null | undefined) => (minor == null ? "—" : formatMoney(minor, "TRY", locale));
}

export const toMinor = (lira: string) => Math.round(Number(lira.replace(",", ".")) * 100);
export const toLira = (minor: number | null | undefined) => (minor == null ? "" : (minor / 100).toFixed(2));

export function useAction() {
  const errorText = useErrorText();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const run = async (action: () => Promise<unknown>, onDone?: () => void) => {
    setError(null);
    setBusy(true);
    try {
      await action();
      onDone?.();
    } catch (e) {
      const details = e instanceof ApiError && e.details ? ` (${Object.entries(e.details).map(([k, v]) => `${k}: ${v.join(", ")}`).join("; ")})` : "";
      setError(`${errorText(e instanceof ApiError ? e.code : undefined)}${e instanceof ApiError ? ` [${e.code}]` : ""}${details}`);
    } finally {
      setBusy(false);
    }
  };
  const view = error ? (
    <p className="notice notice-danger small" role="alert">
      {error}
    </p>
  ) : null;
  return { run, busy, error: view };
}
