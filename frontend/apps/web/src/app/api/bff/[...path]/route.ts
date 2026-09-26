import { cookies } from "next/headers";
import { type NextRequest, NextResponse } from "next/server";
import { config } from "@/lib/server/config";
import { CART_COOKIE, CSRF_COOKIE, SESSION_COOKIE, type Session, seal, sessionCookieOptions, sessionFromTokens, unseal } from "@/lib/server/session";

/**
 * The Backend-for-Frontend (security.md §2). The browser only ever calls /api/bff/*:
 *  - the session cookie becomes an Authorization header (refreshed when it is about to expire);
 *  - the guest cart token lives in an httpOnly cookie and travels as X-Cart-Token;
 *  - every state-changing request must echo the CSRF cookie in a header (double submit);
 *  - sign-in responses are stripped of tokens, which go into the encrypted cookie instead.
 */
const TOKEN_ROUTES = new Set(["auth/otp/verify", "auth/staff/login", "auth/staff/totp/confirm"]);
const FORWARDED_HEADERS = ["content-type", "accept-language", "idempotency-key", "x-locale"];

async function refresh(session: Session): Promise<Session | null> {
  const response = await fetch(`${config.apiUrl}/api/auth/refresh`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ refreshToken: session.refreshToken }),
  });
  if (!response.ok) return null;
  const tokens = (await response.json()) as { accessToken: string; refreshToken: string };
  return sessionFromTokens(tokens, session.name);
}

async function handle(request: NextRequest, { params }: { params: Promise<{ path: string[] }> }) {
  const { path } = await params;
  const route = path.join("/");
  const jar = await cookies();

  if (request.method !== "GET" && request.method !== "HEAD") {
    const csrf = jar.get(CSRF_COOKIE)?.value;
    if (!csrf || request.headers.get("x-rahiq-csrf") !== csrf) {
      return NextResponse.json({ code: "csrf.invalid", status: 403 }, { status: 403 });
    }
  }

  let session = await unseal<Session>(jar.get(SESSION_COOKIE)?.value);
  let sessionChanged = false;
  if (session && session.accessExpiresAt - Date.now() < 30_000) {
    session = await refresh(session);
    sessionChanged = true;
  }

  if (route === "auth/logout") {
    if (session) {
      await fetch(`${config.apiUrl}/api/auth/logout`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ refreshToken: session.refreshToken }) });
    }
    const out = NextResponse.json({ ok: true });
    out.cookies.delete(SESSION_COOKIE);
    return out;
  }

  const headers = new Headers();
  for (const name of FORWARDED_HEADERS) {
    const value = request.headers.get(name);
    if (value) headers.set(name, value);
  }
  if (session) headers.set("Authorization", `Bearer ${session.accessToken}`);
  const cartToken = jar.get(CART_COOKIE)?.value;
  if (cartToken) headers.set("X-Cart-Token", cartToken);
  headers.set("X-Forwarded-For", request.headers.get("x-forwarded-for") ?? "127.0.0.1");

  let body: BodyInit | undefined;
  if (request.method !== "GET" && request.method !== "HEAD") {
    if (route === "auth/otp/verify") {
      const json = (await request.json()) as Record<string, unknown>;
      body = JSON.stringify({ ...json, guestCartToken: cartToken ?? null });
    } else {
      body = await request.arrayBuffer();
    }
  }

  const search = request.nextUrl.search;
  const upstream = await fetch(`${config.apiUrl}/api/${route}${search}`, { method: request.method, headers, body, redirect: "manual" });
  const contentType = upstream.headers.get("content-type") ?? "application/json";

  let response: NextResponse;
  if (TOKEN_ROUTES.has(route) && upstream.ok) {
    const data = (await upstream.json()) as { tokens?: { accessToken: string; refreshToken: string } | null; profile?: { name?: string | null } } & Record<string, unknown>;
    if (data.tokens) {
      session = sessionFromTokens(data.tokens, data.profile?.name);
      sessionChanged = true;
    }
    const rest: Record<string, unknown> = { ...data };
    delete rest.tokens; // Tokens stay in the encrypted cookie, never in the browser.
    response = NextResponse.json(rest, { status: upstream.status });
    if (route === "auth/otp/verify") response.cookies.delete(CART_COOKIE); // Merged into the customer cart.
  } else if (upstream.status === 204) {
    response = new NextResponse(null, { status: 204 });
  } else {
    response = new NextResponse(await upstream.arrayBuffer(), { status: upstream.status, headers: { "content-type": contentType } });
  }

  const newCart = upstream.headers.get("x-cart-token");
  if (newCart) {
    response.cookies.set(CART_COOKIE, newCart, { ...sessionCookieOptions, maxAge: 60 * 60 * 24 * 30 });
  }

  if (sessionChanged) {
    if (session) response.cookies.set(SESSION_COOKIE, await seal(session), sessionCookieOptions);
    else response.cookies.delete(SESSION_COOKIE);
  }

  response.headers.set("Cache-Control", "no-store");
  return response;
}

export { handle as DELETE, handle as GET, handle as PATCH, handle as POST, handle as PUT };
