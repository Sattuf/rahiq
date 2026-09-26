import { cookies } from "next/headers";
import { type NextRequest, NextResponse } from "next/server";
import { config } from "@/lib/server/config";
import { CART_COOKIE, sessionCookieOptions } from "@/lib/server/session";

/**
 * Add to cart without JavaScript (frontend-experience.md §5, level 4): a plain form POST, then back to the page.
 * SameSite=Lax cookies and an Origin check stand in for the CSRF header here.
 */
export async function POST(request: NextRequest) {
  const origin = request.headers.get("origin");
  if (origin && new URL(origin).host !== request.nextUrl.host) {
    return NextResponse.json({ ok: false }, { status: 403 });
  }

  const form = await request.formData();
  const variantId = String(form.get("variantId") ?? "");
  const qty = Math.max(1, Math.min(10, Number(form.get("qty") ?? 1)));
  const back = String(form.get("back") ?? "/");
  const jar = await cookies();
  const token = jar.get(CART_COOKIE)?.value;

  const upstream = await fetch(`${config.apiUrl}/api/cart/lines`, {
    method: "POST",
    headers: { "content-type": "application/json", ...(token ? { "X-Cart-Token": token } : {}) },
    body: JSON.stringify({ variantId, qty }),
  });

  const target = new URL(back.startsWith("/") ? back : "/", request.url);
  target.searchParams.set("added", upstream.ok ? "1" : "0");
  const response = NextResponse.redirect(target, 303);
  const newToken = upstream.headers.get("x-cart-token");
  if (newToken) response.cookies.set(CART_COOKIE, newToken, sessionCookieOptions);
  return response;
}
