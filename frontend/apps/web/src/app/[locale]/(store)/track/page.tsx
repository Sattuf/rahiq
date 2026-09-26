import { type Locale, translator } from "@rahiq/i18n";
import type { Metadata } from "next";
import { TrackOrder } from "@/components/account/TrackOrder";

export const metadata: Metadata = { robots: { index: false } };

export default async function TrackPage({ params, searchParams }: { params: Promise<{ locale: string }>; searchParams: Promise<{ order?: string }> }) {
  const locale = (await params).locale as Locale;
  const { order } = await searchParams;
  const t = translator(locale);
  return (
    <div className="container narrow section">
      <h1>{t("track.title")}</h1>
      <p className="lede">{t("track.intro")}</p>
      <TrackOrder initialNumber={order} />
    </div>
  );
}
