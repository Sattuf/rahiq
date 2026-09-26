import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { PaymentResult } from "@/components/checkout/PaymentResult";

export const metadata: Metadata = { robots: { index: false } };

export default async function ResultPage({ params, searchParams }: { params: Promise<{ locale: string }>; searchParams: Promise<{ order?: string }> }) {
  const { locale } = await params;
  const { order } = await searchParams;
  if (!order) redirect(`/${locale}`);
  return <PaymentResult number={order} />;
}
