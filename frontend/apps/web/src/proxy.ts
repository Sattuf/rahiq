import { type NextRequest, NextResponse } from "next/server";

const LOCALES = ["tr", "ar", "en"];
const DEFAULT_LOCALE = "tr";
const CSRF_COOKIE = "rahiq_csrf";

function pickLocale(request: NextRequest): string {
  const saved = request.cookies.get("rahiq_locale")?.value;
  if (saved && LOCALES.includes(saved)) return saved;
  const accepted = request.headers.get("accept-language")?.split(",").map((l) => l.split(";")[0].trim().slice(0, 2).toLowerCase()) ?? [];
  return accepted.find((l) => LOCALES.includes(l)) ?? DEFAULT_LOCALE;
}

/**
 * Runs before every page (not for /api, assets or images):
 * 1. URLs carry a locale prefix (/tr, /ar, /en; content-seo.md §1); a bare path is redirected, query kept.
 * 2. A fresh CSP nonce per request (security.md §8): scripts run only with it. Payment and media origins are allowed.
 * 3. A CSRF double-submit cookie exists for the BFF.
 */
export function proxy(request: NextRequest) {
  const { pathname, search } = request.nextUrl;
  const first = pathname.split("/")[1];
  if (!LOCALES.includes(first)) {
    const url = request.nextUrl.clone();
    url.pathname = `/${pickLocale(request)}${pathname === "/" ? "" : pathname}`;
    url.search = search;
    return NextResponse.redirect(url, 307);
  }

  const nonce = Buffer.from(crypto.randomUUID()).toString("base64");
  const dev = process.env.NODE_ENV === "development";
  const media = process.env.NEXT_PUBLIC_MEDIA_ORIGIN ?? "http://localhost:5080";
  // The admin's live inbox talks to the API's SignalR hub directly (https + wss on the API origin only).
  const api = (process.env.NEXT_PUBLIC_API_ORIGIN ?? "http://localhost:5080").replace(/\/$/, "");
  const hub = pathname.includes("/admin") ? ` ${api} ${api.replace(/^http/, "ws")}` : "";
  const payment = process.env.PAYMENT_FORM_ORIGINS ?? "https://sandbox-api.iyzipay.com https://api.iyzipay.com https://*.iyzipay.com";
  const csp = [
    "default-src 'self'",
    `script-src 'self' 'nonce-${nonce}' 'strict-dynamic'${dev ? " 'unsafe-eval'" : ""}`,
    "style-src 'self' 'unsafe-inline'", // Inline style attributes set by the motion libraries; scripts stay strict.
    `img-src 'self' blob: data: ${media}`,
    "font-src 'self'",
    `connect-src 'self'${hub}${dev ? " ws:" : ""}`,
    `frame-src ${payment}`,
    "worker-src 'self' blob:",
    "object-src 'none'",
    "base-uri 'self'",
    `form-action 'self' ${payment}`,
    "frame-ancestors 'none'",
    ...(dev ? [] : ["upgrade-insecure-requests"]),
  ].join("; ");

  const requestHeaders = new Headers(request.headers);
  requestHeaders.set("x-nonce", nonce);
  requestHeaders.set("x-locale", first);
  requestHeaders.set("Content-Security-Policy", csp);

  const response = NextResponse.next({ request: { headers: requestHeaders } });
  response.headers.set("Content-Security-Policy", csp);
  response.headers.set("X-Frame-Options", "DENY");
  // Remember the language, so a return from the payment provider (a URL without a locale) lands in the same one.
  if (request.cookies.get("rahiq_locale")?.value !== first) {
    response.cookies.set("rahiq_locale", first, { sameSite: "lax", path: "/", maxAge: 60 * 60 * 24 * 365, secure: !dev });
  }
  if (!request.cookies.get(CSRF_COOKIE)) {
    response.cookies.set(CSRF_COOKIE, crypto.randomUUID().replaceAll("-", ""), { sameSite: "lax", path: "/", secure: !dev, httpOnly: false });
  }

  return response;
}

export const config = {
  matcher: [
    {
      source: "/((?!api|_next/static|_next/image|favicon.ico|icon.svg|robots.txt|sitemap.xml|fallback|.*\\.(?:png|jpg|svg|webp|avif|woff2)$).*)",
      missing: [
        { type: "header", key: "next-router-prefetch" },
        { type: "header", key: "purpose", value: "prefetch" },
      ],
    },
  ],
};
