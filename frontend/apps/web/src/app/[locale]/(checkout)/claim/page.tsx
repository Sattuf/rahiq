import type { Metadata } from "next";
import { ClaimCart } from "@/components/checkout/ClaimCart";

export const metadata: Metadata = { robots: { index: false }, title: "Cart" };

/** Landing page of a checkout link sent by the chat assistant: /{locale}/claim#code (ADR-019). */
export default function ClaimPage() {
  return <ClaimCart />;
}
