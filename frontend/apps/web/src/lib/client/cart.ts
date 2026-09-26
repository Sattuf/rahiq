"use client";

import type { Cart } from "@rahiq/api-client";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { bff } from "./bff";
import { useLocale } from "./i18n";

const KEY = ["cart"];

/** The cart is always recomputed by the server (commerce-flows.md §1); the browser only shows it. */
export function useCart() {
  const locale = useLocale();
  return useQuery({ queryKey: [...KEY, locale], queryFn: () => bff<Cart>("cart", { locale }), staleTime: 10_000 });
}

export function useCartMutations() {
  const client = useQueryClient();
  const locale = useLocale();
  const set = (cart: Cart) => client.setQueryData([...KEY, locale], cart);

  return {
    add: useMutation({
      mutationFn: (input: { variantId: string; qty: number; components?: string[]; giftMessage?: string }) =>
        bff<Cart>("cart/lines", { method: "POST", body: input, locale }),
      onSuccess: set,
    }),
    update: useMutation({
      mutationFn: ({ lineId, qty }: { lineId: string; qty: number }) => bff<Cart>(`cart/lines/${lineId}`, { method: "PATCH", body: { qty }, locale }),
      onSuccess: set,
    }),
    remove: useMutation({
      mutationFn: (lineId: string) => bff<Cart>(`cart/lines/${lineId}`, { method: "DELETE", locale }),
      onSuccess: set,
    }),
    coupon: useMutation({
      mutationFn: (code: string | null) => bff<Cart>("cart/coupon", { method: "PUT", body: { code }, locale }),
      onSuccess: set,
    }),
  };
}

/** Opens the cart drawer from anywhere (a tiny event bus instead of global state). */
export function openCart() {
  window.dispatchEvent(new CustomEvent("rahiq:open-cart"));
}
