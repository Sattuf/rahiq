import type { ProductCard } from "@rahiq/api-client";
import type { MetadataRoute } from "next";

const site = (process.env.NEXT_PUBLIC_SITE_URL ?? "http://localhost:3000").replace(/\/$/, "");
const api = (process.env.API_INTERNAL_URL ?? "http://localhost:5080").replace(/\/$/, "");
const locales = ["tr", "ar", "en"];

/** One entry per page with hreflang alternates for the three languages (content-seo.md §3). Refreshed on publish. */
export default async function sitemap(): Promise<MetadataRoute.Sitemap> {
  let products: ProductCard[] = [];
  try {
    const response = await fetch(`${api}/api/catalog/products?limit=200&locale=tr`, { next: { tags: ["catalog"], revalidate: 3600 } });
    if (response.ok) products = (await response.json()) as ProductCard[];
  } catch {
    // The static pages are still listed when the API is down.
  }

  const paths = ["", "/perfume", "/honey", "/gift-box", "/guide", ...products.map((p) => `/p/${p.slug}`)];
  return paths.map((path) => ({
    url: `${site}/tr${path}`,
    changeFrequency: path.startsWith("/p/") ? "weekly" : "daily",
    priority: path === "" ? 1 : path.startsWith("/p/") ? 0.8 : 0.9,
    alternates: { languages: Object.fromEntries(locales.map((l) => [l, `${site}/${l}${path}`])) },
  }));
}
