"use client";

import type { GuideQuestion, ProductCard } from "@rahiq/api-client";
import { formatMoney } from "@rahiq/ui";
import Link from "next/link";
import { useState } from "react";
import { bff } from "@/lib/client/bff";
import { openCart, useCartMutations } from "@/lib/client/cart";
import { useLocale, useT } from "@/lib/client/i18n";

// Scenes are pictures, not only words (frontend-experience.md §3.2): drawn gradients stand in until the photo shoot.
const SCENE_ART: Record<string, string> = {
  morning_garden: "linear-gradient(160deg,#f3e7d3,#c9d8b6)",
  winter_majlis: "linear-gradient(160deg,#4e3325,#1f1712)",
  seaside: "linear-gradient(160deg,#d6dee2,#8fb0c4)",
  spice_market: "linear-gradient(160deg,#c07e24,#8c2f1f)",
};

export function PerfumeGuide({ questions }: { questions: GuideQuestion[] }) {
  const t = useT();
  const locale = useLocale();
  const { add } = useCartMutations();
  const [step, setStep] = useState(0);
  const [answers, setAnswers] = useState<Record<string, string>>({});
  const [results, setResults] = useState<ProductCard[] | null>(null);
  const question = questions[step];

  const choose = async (code: string) => {
    const next = { ...answers, [question.code]: code };
    setAnswers(next);
    if (step < questions.length - 1) {
      setStep(step + 1);
      return;
    }

    setResults(await bff<ProductCard[]>("guide/recommend", { method: "POST", body: next, locale }));
  };

  const addSamples = async (products: ProductCard[]) => {
    for (const p of products.filter((x) => x.hasSample)) {
      const detail = await bff<{ variants: { id: string; isSample: boolean }[] }>(`catalog/products/${p.slug}`, { locale });
      const sample = detail.variants.find((v) => v.isSample);
      if (sample) await add.mutateAsync({ variantId: sample.id, qty: 1 });
    }
    openCart();
  };

  if (results) {
    return (
      <div className="guide-results" aria-live="polite">
        <h2>{t("guide.result")}</h2>
        {results.length === 0 ? (
          <p>{t("guide.none")}</p>
        ) : (
          <>
            <ul className="grid-products">
              {results.map((p) => (
                <li key={p.id}>
                  <Link className="card-link" href={`/${locale}/p/${p.slug}`}>
                    <strong>{p.name}</strong>
                    <span className="muted">{p.shortDescription}</span>
                    {p.price != null && <span className="price">{formatMoney(p.price, p.currency, locale)}</span>}
                  </Link>
                </li>
              ))}
            </ul>
            <button type="button" className="btn btn-buy" onClick={() => addSamples(results)} disabled={add.isPending}>
              {t("guide.tryThree")}
            </button>
          </>
        )}
        <button
          type="button"
          className="btn btn-quiet"
          onClick={() => {
            setResults(null);
            setAnswers({});
            setStep(0);
          }}
        >
          {t("guide.again")}
        </button>
      </div>
    );
  }

  return (
    <div className="guide">
      <p className="muted">{t("guide.step", { n: step + 1 })}</p>
      <h2>{question.prompt}</h2>
      <div className={`guide-options${question.code === "scene" ? " guide-scenes" : ""}`}>
        {question.options.map((o) => (
          <button
            key={o.code}
            type="button"
            className="guide-option"
            aria-pressed={answers[question.code] === o.code}
            onClick={() => void choose(o.code)}
            style={question.code === "scene" ? { backgroundImage: SCENE_ART[o.code] } : undefined}
          >
            <span>{o.label}</span>
          </button>
        ))}
      </div>
      {step > 0 && (
        <button type="button" className="btn btn-quiet" onClick={() => setStep(step - 1)}>
          {t("guide.back")}
        </button>
      )}
    </div>
  );
}
