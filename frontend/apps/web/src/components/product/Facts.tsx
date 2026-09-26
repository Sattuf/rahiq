import { type Locale, translate } from "@rahiq/i18n";

/** A 1–5 scale as five segments plus the word for it: meaning never depends on colour alone (§7). */
export function ScaleBar({ label, value, locale }: { label: string; value?: number | null; locale: Locale }) {
  if (!value) return null;
  return (
    <div className="scale-row">
      <div className="scale-head">
        <span>{label}</span>
        <span className="muted small">{translate(locale, `product.scale.${value}`)}</span>
      </div>
      <div className="scale-bar" role="img" aria-label={`${label}: ${value}/5`}>
        {[1, 2, 3, 4, 5].map((n) => (
          <span key={n} data-on={n <= value} />
        ))}
      </div>
    </div>
  );
}

/** Mandatory warnings, rendered from data in a fixed place on every product that has them (Law 2). */
export function Warnings({ warnings, locale }: { warnings: { code: string; text: string }[]; locale: Locale }) {
  if (warnings.length === 0) return null;
  return (
    <section className="warnings" aria-labelledby="warnings-title">
      <h2 id="warnings-title" className="h-small">
        {translate(locale, "product.warnings")}
      </h2>
      <ul>
        {warnings.map((w) => (
          <li key={w.code}>{w.text}</li>
        ))}
      </ul>
    </section>
  );
}

export function AllergenBox({ allergens, locale }: { allergens: { code: string; name: string }[]; locale: Locale }) {
  if (allergens.length === 0) return null;
  return (
    <div className="allergen-box" role="note">
      {translate(locale, "product.allergenBox")}: {allergens.map((a) => a.name).join(", ")}
    </div>
  );
}
