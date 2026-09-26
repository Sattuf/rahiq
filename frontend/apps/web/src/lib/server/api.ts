import "server-only";

import { ApiError, toApiError } from "@rahiq/api-client";
import type { Locale } from "@rahiq/i18n";
import { cookies } from "next/headers";
import { config } from "./config";
import { CART_COOKIE, readSession } from "./session";

type Options = {
  locale: Locale;
  /** Cache tags for storefront reads; revalidated on publish and price changes (architecture.md §6). */
  tags?: string[];
  revalidate?: number | false;
  /** Personal data (account, cart): never cached, sent with the session. */
  personal?: boolean;
};

/** Server-side reads from the API. Public catalog data is cached by tag; personal data never is. */
export async function apiGet<T>(path: string, { locale, tags, revalidate = 300, personal }: Options): Promise<T> {
  const headers: Record<string, string> = { "Accept-Language": locale };
  if (personal) {
    const session = await readSession();
    if (session) headers.Authorization = `Bearer ${session.accessToken}`;
    const cart = (await cookies()).get(CART_COOKIE)?.value;
    if (cart) headers["X-Cart-Token"] = cart;
  }

  const separator = path.includes("?") ? "&" : "?";
  const response = await fetch(`${config.apiUrl}${path}${separator}locale=${locale}`, {
    headers,
    ...(personal ? { cache: "no-store" as const } : { next: { tags: tags ?? ["catalog"], revalidate } }),
    redirect: "manual",
  });

  if (response.status === 301) {
    const moved = (await response.json()) as { newSlug: string };
    throw new MovedError(moved.newSlug);
  }

  if (!response.ok) {
    throw await toApiError(response);
  }

  return (await response.json()) as T;
}

export async function apiGetOrNull<T>(path: string, options: Options): Promise<T | null> {
  try {
    return await apiGet<T>(path, options);
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null;
    throw error;
  }
}

export class MovedError extends Error {
  constructor(public readonly slug: string) {
    super("moved");
  }
}
