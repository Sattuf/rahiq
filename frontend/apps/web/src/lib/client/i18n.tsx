"use client";

import { type Locale, type Messages, type T, translateWith } from "@rahiq/i18n/core";
import { createContext, type ReactNode, useContext, useMemo } from "react";

type Context = { locale: Locale; messages: Messages };

const LocaleContext = createContext<Context | null>(null);

/** Receives the one dictionary this page needs from the server; the other languages never reach the browser. */
export function LocaleProvider({ locale, messages, children }: { locale: Locale; messages: Messages; children: ReactNode }) {
  const value = useMemo(() => ({ locale, messages }), [locale, messages]);
  return <LocaleContext.Provider value={value}>{children}</LocaleContext.Provider>;
}

function useContextValue(): Context {
  const value = useContext(LocaleContext);
  if (!value) throw new Error("LocaleProvider missing");
  return value;
}

export function useLocale(): Locale {
  return useContextValue().locale;
}

export function useT(): T {
  const { locale, messages } = useContextValue();
  return useMemo<T>(() => (key, values) => translateWith(messages, locale, key, values), [locale, messages]);
}

/** Translates an API error code; falls back to the generic message. */
export function useErrorText() {
  const t = useT();
  return (code: string | undefined) => {
    if (!code) return t("common.error");
    const text = t(`errors.codes.${code}`);
    return text === `errors.codes.${code}` ? t("common.error") : text;
  };
}
