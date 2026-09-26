"use client";

import type { Taxonomy } from "@rahiq/api-client";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { use, useState } from "react";
import { toMinor, useAction } from "@/components/admin/ui";
import { bff } from "@/lib/client/bff";
import { useLocale, useT } from "@/lib/client/i18n";

type Translation = { locale: string; name: string; shortDescription?: string | null; story?: string | null; usage?: string | null; seoTitle?: string | null; seoDescription?: string | null };
type AdminVariant = { id: string; sku: string; volumeMl?: number | null; weightG?: number | null; gtin?: string | null; shippingWeightG: number; shippingClass: string; isSample: boolean; isActive: boolean; sortOrder: number };
type Finding = { term: string; field: string; severity: "blocking" | "advisory" };
type AdminProduct = {
  id: string; section: string; type: string; slug: string; status: string; version: number; attributes: unknown; warnings: string[]; allergens: string[];
  isFeatured: boolean; sortOrder: number; translations: Translation[]; variants: AdminVariant[]; media: { id: string; url: string; role: string }[];
  bundleItems: { componentVariantId: string; qty: number }[]; giftSlots: { code: string; allowedSections: string[]; required: boolean; sortOrder: number }[];
  findings: Finding[]; readinessProblems: string[];
};

const LOCALES = ["tr", "ar", "en"] as const;
const TEMPLATES: Record<string, object> = {
  perfume: { concentration: "edp", families: ["oriental"], gender: "unisex", seasons: ["autumn"], notes: { top: [], heart: [], base: [] }, intensity: 3, longevity: 3, sillage: 3, inci: [], declaredAllergens: [] },
  honey: { floralSource: "multifloral", regionCode: "black_sea", region: { tr: "", ar: "", en: "" }, taste: { sweetness: 3, bitterness: 2, intensity: 3 }, texture: "liquid", colorScale: 3 },
  nuts_in_honey: { honeyType: "multifloral", composition: [{ ingredient: { tr: "Bal", ar: "عسل", en: "Honey" }, percent: 60 }] },
  honey_blend: { honeyType: "multifloral", composition: [{ ingredient: { tr: "Bal", ar: "عسل", en: "Honey" }, percent: 90 }], suggestedUse: { tr: "", ar: "", en: "" } },
};

export default function AdminProduct({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const t = useT();
  const { data: p } = useQuery({ queryKey: ["admin", "product", id], queryFn: () => bff<AdminProduct>(`admin/products/${id}`) });
  if (!p) return <p className="muted">{t("common.loading")}</p>;
  // Re-mount the editor whenever the saved version changes, so its fields start from the server's truth.
  return <Editor key={p.version} id={id} p={p} />;
}

function Editor({ id, p }: { id: string; p: AdminProduct }) {
  const t = useT();
  const locale = useLocale();
  const client = useQueryClient();
  const { run, busy, error } = useAction();
  const { data: taxonomy } = useQuery({ queryKey: ["taxonomy", locale], queryFn: () => bff<Taxonomy>("catalog/taxonomy", { locale }) });
  const [tab, setTab] = useState<(typeof LOCALES)[number]>("tr");
  const [texts, setTexts] = useState<Record<string, Translation>>(() =>
    Object.fromEntries(LOCALES.map((l) => [l, p.translations.find((x) => x.locale === l) ?? { locale: l, name: "" }])),
  );
  const [attributes, setAttributes] = useState(() => JSON.stringify(Object.keys(p.attributes as object).length ? p.attributes : (TEMPLATES[p.type] ?? {}), null, 2));
  const [slug, setSlug] = useState(p.slug);
  const [allergens, setAllergens] = useState<string[]>(p.allergens);
  const [featured, setFeatured] = useState(p.isFeatured);
  const [findings, setFindings] = useState<Finding[]>(p.findings);
  const [problems, setProblems] = useState<string[]>(p.readinessProblems);
  const [approve, setApprove] = useState({ on: false, reason: "" });
  const [prices, setPrices] = useState<Record<string, string>>({});

  const reload = () => client.invalidateQueries({ queryKey: ["admin", "product", id] });

  const save = () =>
    run(async () => {
      const saved = await bff<{ version: number; findings: Finding[]; readinessProblems: string[] }>(`admin/products/${id}`, {
        method: "PUT",
        body: {
          version: p.version,
          slug,
          translations: LOCALES.map((l) => texts[l]).filter((x) => x?.name?.trim()),
          attributes: JSON.parse(attributes),
          warnings: p.warnings,
          allergens,
          isFeatured: featured,
          sortOrder: p.sortOrder,
        },
      });
      setFindings(saved.findings);
      setProblems(saved.readinessProblems);
      await reload();
    });

  const setText = (field: keyof Translation, value: string) => setTexts((s) => ({ ...s, [tab]: { ...s[tab], locale: tab, [field]: value } }));

  return (
    <>
      <div className="admin-head">
        <h1>{texts.tr?.name || p.slug}</h1>
        <span className={`status status-${p.status}`}>{p.status}</span>
        <span className="muted small">{p.type}</span>
      </div>
      {error}

      <section className="panel" aria-live="polite">
        <h2 className="h-small">{t("admin.findings")}</h2>
        {findings.length === 0 && <p className="muted small">✓</p>}
        <ul className="findings">
          {findings.map((f, i) => (
            <li key={i} className={f.severity === "blocking" ? "field-error" : "muted"}>
              {f.severity === "blocking" ? t("admin.blocking") : t("admin.advisory")}: <strong>{f.term}</strong> · {f.field}
            </li>
          ))}
        </ul>
        {problems.length > 0 && (
          <p className="small">
            {t("admin.readiness")}: {problems.join(", ")}
          </p>
        )}
      </section>

      <section className="panel">
        <h2 className="h-small">{t("admin.translations")}</h2>
        <div className="chips" role="tablist">
          {LOCALES.map((l) => (
            <button key={l} type="button" role="tab" className="chip" aria-selected={tab === l} onClick={() => setTab(l)}>
              {l.toUpperCase()}
            </button>
          ))}
        </div>
        <div className="form-grid" dir={tab === "ar" ? "rtl" : "ltr"} lang={tab}>
          {(["name", "shortDescription", "story", "usage", "seoTitle", "seoDescription"] as const).map((field) => (
            <div key={field} className={`field ${field === "story" || field === "usage" ? "span-2" : ""}`}>
              <label htmlFor={`${tab}-${field}`}>{field}</label>
              {field === "story" || field === "usage" ? (
                <textarea id={`${tab}-${field}`} className="textarea" value={texts[tab]?.[field] ?? ""} onChange={(e) => setText(field, e.target.value)} />
              ) : (
                <input id={`${tab}-${field}`} className="input" value={texts[tab]?.[field] ?? ""} onChange={(e) => setText(field, e.target.value)} />
              )}
            </div>
          ))}
        </div>
        <div className="form-grid">
          <div className="field">
            <label htmlFor="slug">{t("admin.slug")}</label>
            <input id="slug" className="input" value={slug} onChange={(e) => setSlug(e.target.value)} />
          </div>
          <label className="check">
            <input type="checkbox" checked={featured} onChange={(e) => setFeatured(e.target.checked)} /> featured
          </label>
          <fieldset className="span-2">
            <legend>{t("product.allergens")}</legend>
            {taxonomy?.allergens.map((a) => (
              <label key={a.code} className="check">
                <input type="checkbox" checked={allergens.includes(a.code)} onChange={(e) => setAllergens((s) => (e.target.checked ? [...s, a.code] : s.filter((x) => x !== a.code)))} />
                {a.name}
              </label>
            ))}
          </fieldset>
          <p className="muted small span-2">
            {t("product.warnings")}: {p.warnings.join(", ")}
          </p>
          <div className="field span-2">
            <label htmlFor="attributes">{t("admin.attributes")}</label>
            <textarea id="attributes" className="textarea code" rows={12} value={attributes} onChange={(e) => setAttributes(e.target.value)} spellCheck={false} />
          </div>
        </div>
        <button className="btn btn-buy" type="button" disabled={busy} onClick={() => void save()}>
          {t("admin.save")}
        </button>
      </section>

      <section className="panel">
        <h2 className="h-small">{t("admin.variants")}</h2>
        <table className="admin-table">
          <thead>
            <tr>
              <th>SKU</th>
              <th>ml / g</th>
              <th>ship g</th>
              <th>class</th>
              <th>{t("common.sample")}</th>
              <th>{t("admin.price")}</th>
            </tr>
          </thead>
          <tbody>
            {p.variants.map((v) => (
              <tr key={v.id}>
                <td>{v.sku}</td>
                <td>{v.volumeMl ?? v.weightG}</td>
                <td>{v.shippingWeightG}</td>
                <td>{v.shippingClass}</td>
                <td>{v.isSample ? "✓" : ""}</td>
                <td>
                  <form
                    className="inline-form"
                    onSubmit={(e) => {
                      e.preventDefault();
                      void run(() => bff(`admin/pricing/variants/${v.id}/price`, { method: "PUT", body: { amount: toMinor(prices[v.id] ?? "0") } }), () => setPrices((s) => ({ ...s, [v.id]: "" })));
                    }}
                  >
                    <input className="input" inputMode="decimal" placeholder="₺" value={prices[v.id] ?? ""} onChange={(e) => setPrices((s) => ({ ...s, [v.id]: e.target.value }))} aria-label={t("admin.price")} />
                    <button className="btn" type="submit">
                      {t("admin.save")}
                    </button>
                  </form>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        <VariantForm productId={id} type={p.type} onDone={() => void reload()} />
        {p.type === "bundle" && p.variants[0] && <JsonEditor label="bundle" initial={p.bundleItems} save={(v) => bff(`admin/products/${id}/variants/${p.variants[0].id}/bundle`, { method: "PUT", body: v })} />}
        {p.type === "gift_box" && <JsonEditor label="gift slots" initial={p.giftSlots.length ? p.giftSlots : [{ code: "perfume", allowedSections: ["perfume"], required: true, sortOrder: 0 }, { code: "honey", allowedSections: ["honey"], required: true, sortOrder: 1 }]} save={(v) => bff(`admin/products/${id}/gift-slots`, { method: "PUT", body: v })} />}
      </section>

      <section className="panel">
        <h2 className="h-small">{t("admin.media")}</h2>
        <div className="media-grid">
          {p.media.map((m) => (
            <figure key={m.id}>
              {/* eslint-disable-next-line @next/next/no-img-element */}
              <img src={m.url} alt="" width={120} height={150} />
              <figcaption className="small">{m.role}</figcaption>
              <button className="btn btn-quiet" type="button" onClick={() => run(() => bff(`admin/products/${id}/media/${m.id}`, { method: "DELETE" }), () => void reload())}>
                {t("common.remove")}
              </button>
            </figure>
          ))}
        </div>
        <form
          className="inline-form"
          onSubmit={(e) => {
            e.preventDefault();
            const form = new FormData(e.currentTarget);
            form.set("isGenerated", form.get("isGenerated") ? "true" : "false");
            form.set("sortOrder", String(p.media.length));
            void run(() => bff(`admin/products/${id}/media`, { method: "POST", form }), () => void reload());
          }}
        >
          <input type="file" name="file" accept="image/jpeg,image/png,image/webp" required />
          <select name="role" className="select" defaultValue="catalog">
            {["catalog", "detail", "scale", "packaging", "mood"].map((r) => (
              <option key={r}>{r}</option>
            ))}
          </select>
          <input name="altTr" className="input" placeholder="alt (tr)" />
          <label className="check small">
            <input type="checkbox" name="isGenerated" /> {t("admin.generated")}
          </label>
          <button className="btn" type="submit" disabled={busy}>
            {t("admin.upload")}
          </button>
        </form>
      </section>

      <section className="panel">
        <h2 className="h-small">{t("admin.publish")}</h2>
        {findings.some((f) => f.severity === "blocking") && (
          <>
            <label className="check">
              <input type="checkbox" checked={approve.on} onChange={(e) => setApprove({ ...approve, on: e.target.checked })} />
              {t("admin.approveClaims")}
            </label>
            {approve.on && <textarea className="textarea" placeholder={t("admin.approvalReason")} value={approve.reason} onChange={(e) => setApprove({ ...approve, reason: e.target.value })} />}
          </>
        )}
        <div className="admin-actions">
          <button className="btn btn-buy" disabled={busy} onClick={() => run(() => bff(`admin/products/${id}/publish`, { method: "POST", body: { approveClaims: approve.on, approvalReason: approve.reason } }), () => void reload())}>
            {t("admin.publish")}
          </button>
          <button className="btn" disabled={busy} onClick={() => run(() => bff(`admin/products/${id}/status`, { method: "POST", body: { status: "draft" } }), () => void reload())}>
            {t("admin.unpublish")}
          </button>
          <button className="btn btn-danger" disabled={busy} onClick={() => run(() => bff(`admin/products/${id}/status`, { method: "POST", body: { status: "archived" } }), () => void reload())}>
            {t("admin.archive")}
          </button>
        </div>
      </section>
    </>
  );
}

function VariantForm({ productId, type, onDone }: { productId: string; type: string; onDone: () => void }) {
  const t = useT();
  const { run, busy, error } = useAction();
  const perfume = ["perfume", "attar", "hair_home_mist", "discovery_set"].includes(type);
  return (
    <form
      className="inline-form"
      onSubmit={(e) => {
        e.preventDefault();
        const f = new FormData(e.currentTarget);
        const size = Number(f.get("size"));
        void run(
          () =>
            bff(`admin/products/${productId}/variants`, {
              method: "POST",
              body: {
                sku: f.get("sku"),
                volumeMl: perfume ? size : null,
                weightG: perfume ? null : size,
                gtin: f.get("gtin") || null,
                shippingWeightG: Number(f.get("shipG")),
                shippingClass: f.get("class"),
                isSample: f.get("sample") === "on",
                isActive: true,
                sortOrder: 0,
              },
            }),
          onDone,
        );
      }}
    >
      <strong>{t("admin.addVariant")}</strong>
      <input name="sku" className="input" required placeholder="SKU" />
      <input name="size" className="input" required inputMode="numeric" placeholder={perfume ? "ml" : "g"} />
      <input name="shipG" className="input" required inputMode="numeric" placeholder="ship g" />
      <input name="gtin" className="input" placeholder="GTIN" />
      <select name="class" className="select" defaultValue={type === "perfume" || type === "hair_home_mist" ? "flammable" : "liquid"}>
        {["standard", "fragile", "liquid", "flammable"].map((c) => (
          <option key={c}>{c}</option>
        ))}
      </select>
      {perfume && (
        <label className="check small">
          <input type="checkbox" name="sample" /> {t("common.sample")}
        </label>
      )}
      <button className="btn" type="submit" disabled={busy}>
        {t("common.add")}
      </button>
      {error}
    </form>
  );
}

function JsonEditor({ label, initial, save }: { label: string; initial: unknown; save: (value: unknown) => Promise<unknown> }) {
  const t = useT();
  const [text, setText] = useState(JSON.stringify(initial, null, 2));
  const { run, busy, error } = useAction();
  return (
    <div className="field">
      <label>{label}</label>
      <textarea className="textarea code" rows={6} value={text} onChange={(e) => setText(e.target.value)} spellCheck={false} />
      <button className="btn" type="button" disabled={busy} onClick={() => run(() => save(JSON.parse(text)))}>
        {t("admin.save")}
      </button>
      {error}
    </div>
  );
}
