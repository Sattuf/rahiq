import type { GuideQuestion } from "@rahiq/api-client";
import { type Locale, translator } from "@rahiq/i18n";
import type { Metadata } from "next";
import { PerfumeGuide } from "@/components/world/PerfumeGuide";
import { CausticsLayer } from "@/components/world/CausticsLayer";
import { apiGet } from "@/lib/server/api";

export async function generateMetadata({ params }: { params: Promise<{ locale: string }> }): Promise<Metadata> {
  const locale = (await params).locale as Locale;
  const t = translator(locale);
  return { title: t("guide.title"), description: t("guide.intro") };
}

export default async function GuidePage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = (await params).locale as Locale;
  const t = translator(locale);
  const questions = await apiGet<GuideQuestion[]>("/api/guide/questions", { locale, revalidate: 3600 });
  return (
    <div data-world="perfume" className="guide-page">
      <CausticsLayer world="perfume" />
      <div className="container narrow section">
        <h1>{t("guide.title")}</h1>
        <p className="lede">{t("guide.intro")}</p>
        <PerfumeGuide questions={questions} />
      </div>
    </div>
  );
}
