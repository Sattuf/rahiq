import type { ProductDetail } from "@rahiq/api-client";
import { ImageResponse } from "next/og";

export const size = { width: 1200, height: 630 };
export const contentType = "image/png";

const api = (process.env.API_INTERNAL_URL ?? "http://localhost:5080").replace(/\/$/, "");

/** A share image per product on its section's light (content-seo.md §3): evening for perfume, morning for honey. */
export default async function OpenGraphImage({ params }: { params: Promise<{ locale: string; slug: string }> }) {
  const { locale, slug } = await params;
  let name = "Rahiq";
  let section = "shared";
  try {
    const response = await fetch(`${api}/api/catalog/products/${encodeURIComponent(slug)}?locale=${locale}`, { next: { tags: [`product:${slug}`], revalidate: 3600 } });
    if (response.ok) {
      const product = (await response.json()) as ProductDetail;
      name = product.card.name;
      section = product.card.section;
    }
  } catch {
    // Brand-only card.
  }

  const light = section === "perfume"
    ? { bg: "#343858", glow: "rgba(185,198,240,.55)", ink: "#ECE9E3" }
    : section === "honey"
      ? { bg: "#F1EADB", glow: "rgba(255,199,102,.75)", ink: "#2A1F17" }
      : { bg: "#ECE9E3", glow: "rgba(138,84,18,.25)", ink: "#2A1F17" };

  return new ImageResponse(
    (
      <div style={{ width: "100%", height: "100%", display: "flex", flexDirection: "column", justifyContent: "flex-end", padding: 72, background: `radial-gradient(70% 60% at 30% 30%, ${light.glow}, transparent 70%), ${light.bg}`, color: light.ink }}>
        <div style={{ fontSize: 28, letterSpacing: 8 }}>RAHIQ</div>
        <div style={{ fontSize: 72, lineHeight: 1.1, marginTop: 16 }}>{name}</div>
      </div>
    ),
    size,
  );
}
