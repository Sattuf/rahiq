"use client";

import { ApiError, idempotencyKey, toApiError } from "@rahiq/api-client";

function csrf(): string {
  return document.cookie.split("; ").find((c) => c.startsWith("rahiq_csrf="))?.split("=")[1] ?? "";
}

type Options = { method?: string; body?: unknown; idempotent?: string | boolean; locale?: string; form?: FormData };

/** Calls the API through the BFF. Throws ApiError with the API's stable error code. */
export async function bff<T>(path: string, { method = "GET", body, idempotent, locale, form }: Options = {}): Promise<T> {
  const headers: Record<string, string> = {};
  if (method !== "GET") headers["x-rahiq-csrf"] = csrf();
  if (locale) headers["accept-language"] = locale;
  if (body !== undefined) headers["content-type"] = "application/json";
  if (idempotent) headers["idempotency-key"] = typeof idempotent === "string" ? idempotent : idempotencyKey();

  const separator = path.includes("?") ? "&" : "?";
  const response = await fetch(`/api/bff/${path.replace(/^\//, "")}${locale ? `${separator}locale=${locale}` : ""}`, {
    method,
    headers,
    body: form ?? (body === undefined ? undefined : JSON.stringify(body)),
    credentials: "same-origin",
  });

  if (!response.ok) throw await toApiError(response);
  if (response.status === 204) return undefined as T;
  const type = response.headers.get("content-type") ?? "";
  return (type.includes("json") ? await response.json() : await response.text()) as T;
}

export { ApiError };
