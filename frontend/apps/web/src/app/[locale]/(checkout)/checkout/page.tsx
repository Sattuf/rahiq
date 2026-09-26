import type { Metadata } from "next";
import { Checkout } from "@/components/checkout/Checkout";

export const metadata: Metadata = { robots: { index: false }, title: "Checkout" };

export default function CheckoutPage() {
  return <Checkout />;
}
