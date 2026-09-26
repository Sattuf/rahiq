import "../globals.css";
import "../components.css";
import { dirOf, isLocale, type Locale, locales, messages } from "@rahiq/i18n";
import type { Metadata, Viewport } from "next";
import { El_Messiri, Figtree, Fraunces, IBM_Plex_Sans_Arabic } from "next/font/google";
import { headers } from "next/headers";
import { notFound } from "next/navigation";
import type { ReactNode } from "react";
import { Providers } from "@/components/layout/Providers";
import { config } from "@/lib/server/config";

// Self-hosted by next/font, only the weights used, latin-ext for Turkish (frontend-experience.md §4).
// No font is preloaded. Chrome holds first paint for preloaded fonts: on slow 4G that moved FCP/LCP from ~1.1 s to
// ~3 s (measured on a static copy of the page, no JS; ADR-018 §5). With `swap` and next/font's metric-matched
// fallbacks, text paints at once and the swap causes no layout shift (CLS 0).
const fraunces = Fraunces({ subsets: ["latin", "latin-ext"], variable: "--font-fraunces", display: "swap", preload: false, axes: ["opsz"] });
const figtree = Figtree({ subsets: ["latin", "latin-ext"], variable: "--font-figtree", display: "swap", preload: false, weight: ["400", "500", "600", "700"] });
const messiri = El_Messiri({ subsets: ["arabic", "latin"], variable: "--font-messiri", display: "swap", preload: false, weight: ["400", "500", "600"] });
const plexArabic = IBM_Plex_Sans_Arabic({ subsets: ["arabic"], variable: "--font-plex-arabic", display: "swap", preload: false, weight: ["400", "500", "600"] });

export function generateStaticParams() {
  return locales.map((locale) => ({ locale }));
}

export async function generateMetadata({ params }: { params: Promise<{ locale: string }> }): Promise<Metadata> {
  const { locale } = await params;
  const m = messages(isLocale(locale) ? locale : "tr");
  return {
    metadataBase: new URL(config.siteUrl),
    title: { default: m.meta.title, template: `%s · Rahiq` },
    description: m.meta.description,
    icons: { icon: "/icon.svg" },
    alternates: { languages: Object.fromEntries(locales.map((l) => [l, `/${l}`])) },
    openGraph: { siteName: "Rahiq", locale, type: "website" },
  };
}

export const viewport: Viewport = { themeColor: "#ECE9E3", width: "device-width", initialScale: 1 };

export default async function LocaleLayout({ children, params }: { children: ReactNode; params: Promise<{ locale: string }> }) {
  const { locale } = await params;
  if (!isLocale(locale)) notFound();
  await headers(); // Per-request rendering: the CSP nonce set by the proxy is applied to Next's scripts.

  return (
    <html lang={locale} dir={dirOf(locale as Locale)} className={`${fraunces.variable} ${figtree.variable} ${messiri.variable} ${plexArabic.variable}`}>
      <body>
        <Providers locale={locale as Locale} messages={messages(locale as Locale)}>
          {children}
        </Providers>
      </body>
    </html>
  );
}
