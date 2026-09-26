import type { PublicBatch } from "@rahiq/api-client";
import { type Locale, translator } from "@rahiq/i18n";
import { formatDate } from "@rahiq/ui";
import type { Metadata } from "next";
import Link from "next/link";
import { apiGetOrNull } from "@/lib/server/api";

type Props = { params: Promise<{ locale: string; token: string }> };

export const metadata: Metadata = { robots: { index: false } };

/** What the QR on the jar opens (frontend-experience.md §3.6): tiny, fast on a phone in the kitchen, facts only. */
export default async function BatchPage({ params }: Props) {
  const { locale: raw, token } = await params;
  const locale = raw as Locale;
  const t = translator(locale);
  const batch = await apiGetOrNull<PublicBatch>(`/api/batches/${encodeURIComponent(token)}`, { locale, tags: [`batch:${token}`], revalidate: 600 });

  if (!batch) {
    return (
      <div className="container narrow section">
        <h1>{t("batch.title", { code: "—" })}</h1>
        <p>{t("batch.notFound")}</p>
      </div>
    );
  }

  const lab = batch.labSummary;
  const origin = batch.origin as { region?: string; altitude?: number; season?: string; beekeeper?: string } | null | undefined;

  return (
    <div data-world="honey">
      <div className="container narrow section batch-page">
        <p className="eyebrow">{t("batch.intro")}</p>
        <h1>{t("batch.title", { code: batch.code })}</h1>
        <p className="lede">
          {batch.productName} · {batch.variantLabel}
        </p>

        <dl className="fact-list">
          {origin?.region && (
            <div>
              <dt>{t("product.origin")}</dt>
              <dd>{origin.region}</dd>
            </div>
          )}
          {origin?.altitude && (
            <div>
              <dt>{t("product.altitude")}</dt>
              <dd>{origin.altitude} m</dd>
            </div>
          )}
          {origin?.season && (
            <div>
              <dt>{t("batch.season")}</dt>
              <dd>{origin.season}</dd>
            </div>
          )}
          {origin?.beekeeper && (
            <div>
              <dt>{t("batch.beekeeper")}</dt>
              <dd>{origin.beekeeper}</dd>
            </div>
          )}
          {batch.producedAt && (
            <div>
              <dt>{t("product.produced")}</dt>
              <dd>{formatDate(batch.producedAt, locale)}</dd>
            </div>
          )}
          {batch.bestBefore && (
            <div>
              <dt>{t("product.bestBefore")}</dt>
              <dd>{formatDate(batch.bestBefore, locale)}</dd>
            </div>
          )}
        </dl>

        <section aria-labelledby="lab-title" className="batch-card">
          <h2 id="lab-title" className="h-small">
            {t("batch.summary")}
          </h2>
          {lab ? (
            <ul className="lab-list">
              {lab.moisture != null && <li>{t("batch.moisture", { value: lab.moisture })}</li>}
              {lab.hmf != null && <li>{t("batch.hmf", { value: lab.hmf })}</li>}
              {lab.diastase != null && <li>{t("batch.diastase", { value: lab.diastase })}</li>}
              {lab.pollen && <li>{t("batch.pollen", { name: lab.pollen.name, percent: lab.pollen.percent })}</li>}
              {lab.lab && <li className="muted">{t("batch.lab", { lab: lab.lab, date: lab.reportDate ?? "" })}</li>}
            </ul>
          ) : null}
          <p className={batch.analysed ? "badge-analysed" : "muted"}>{batch.analysed ? t("product.analysed") : t("product.notAnalysed")}</p>
          {batch.analysed && (
            <a className="btn" href={`/api/bff/batches/${encodeURIComponent(token)}/report`}>
              {t("batch.report")}
            </a>
          )}
        </section>

        {batch.available ? (
          <Link className="btn btn-buy" href={`/${locale}/p/${batch.slug}`}>
            {t("batch.orderSame")}
          </Link>
        ) : (
          <p className="notice">
            {t("batch.notCurrent")} <Link href={`/${locale}/p/${batch.slug}`}>{batch.productName} →</Link>
          </p>
        )}
      </div>
    </div>
  );
}
