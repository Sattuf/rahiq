import "server-only";

import { cookies } from "next/headers";
import { config } from "./config";

/**
 * BFF session (security.md §2): the API's access and refresh tokens live only in this encrypted, httpOnly,
 * SameSite=Lax cookie. Browser JavaScript never sees a token.
 */
export type Session = {
  kind: "customer" | "staff";
  email: string;
  name?: string | null;
  permissions?: string[];
  accessToken: string;
  accessExpiresAt: number;
  refreshToken: string;
};

export const SESSION_COOKIE = "rahiq_session";
export const CART_COOKIE = "rahiq_cart";
export const CSRF_COOKIE = "rahiq_csrf";

const encoder = new TextEncoder();
const decoder = new TextDecoder();

async function key(): Promise<CryptoKey> {
  const digest = await crypto.subtle.digest("SHA-256", encoder.encode(config.sessionSecret));
  return crypto.subtle.importKey("raw", digest, "AES-GCM", false, ["encrypt", "decrypt"]);
}

const b64 = (bytes: Uint8Array) => Buffer.from(bytes).toString("base64url");
const unb64 = (text: string) => new Uint8Array(Buffer.from(text, "base64url"));

export async function seal(value: unknown): Promise<string> {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const data = new Uint8Array(await crypto.subtle.encrypt({ name: "AES-GCM", iv }, await key(), encoder.encode(JSON.stringify(value))));
  return `${b64(iv)}.${b64(data)}`;
}

export async function unseal<T>(sealed: string | undefined): Promise<T | null> {
  if (!sealed) return null;
  try {
    const [iv, data] = sealed.split(".");
    const plain = await crypto.subtle.decrypt({ name: "AES-GCM", iv: unb64(iv) }, await key(), unb64(data));
    return JSON.parse(decoder.decode(plain)) as T;
  } catch {
    return null; // Tampered, or the secret rotated: treat as signed out.
  }
}

export const sessionCookieOptions = {
  httpOnly: true,
  secure: config.secureCookies,
  sameSite: "lax" as const,
  path: "/",
  maxAge: 60 * 60 * 24 * 30,
};

export async function readSession(): Promise<Session | null> {
  const jar = await cookies();
  return unseal<Session>(jar.get(SESSION_COOKIE)?.value);
}

/** Decodes the payload of our own API's JWT (already verified by the API) to read expiry and permissions. */
export function decodeJwt(token: string): Record<string, unknown> {
  const [, payload] = token.split(".");
  return JSON.parse(Buffer.from(payload, "base64url").toString("utf8")) as Record<string, unknown>;
}

export function sessionFromTokens(tokens: { accessToken: string; refreshToken: string }, name?: string | null): Session {
  const claims = decodeJwt(tokens.accessToken);
  const perms = claims.perm;
  return {
    kind: claims.kind === "staff" ? "staff" : "customer",
    email: String(claims.email ?? ""),
    name: (claims.name as string | undefined) ?? name ?? null,
    permissions: Array.isArray(perms) ? (perms as string[]) : typeof perms === "string" ? [perms] : [],
    accessToken: tokens.accessToken,
    accessExpiresAt: Number(claims.exp ?? 0) * 1000,
    refreshToken: tokens.refreshToken,
  };
}
