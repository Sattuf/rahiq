"use client";

import type { Locale, Messages } from "@rahiq/i18n/core";
import type { ReactNode } from "react";
import { LocaleProvider } from "@/lib/client/i18n";

/** Everywhere: the locale and its dictionary. */
export function Providers({ locale, messages, children }: { locale: Locale; messages: Messages; children: ReactNode }) {
  return (
    <LocaleProvider locale={locale} messages={messages}>
      {children}
    </LocaleProvider>
  );
}
