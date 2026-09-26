/**
 * The mark (brand-identity.md §4): one drop that is both a drop of honey and a drop of perfume, holding a
 * six-petal flower drawn as a single continuous line (a rose curve r = |cos 3θ|), a quiet nod to the hexagon
 * without drawing a honeycomb. Works in one colour, at 16 px, hot-foiled or engraved.
 */
function flowerPath(cx: number, cy: number, radius: number): string {
  const points: string[] = [];
  for (let i = 0; i <= 360; i++) {
    const t = (i / 360) * Math.PI * 2;
    const r = radius * Math.abs(Math.cos(3 * t));
    points.push(`${(cx + r * Math.cos(t)).toFixed(2)} ${(cy + r * Math.sin(t)).toFixed(2)}`);
  }
  return `M${points.join(" L")}Z`;
}

export const DROP_PATH = "M50 6 C50 6 16 46 16 68 A34 34 0 0 0 84 68 C84 46 50 6 50 6 Z";
export const FLOWER_PATH = flowerPath(50, 67, 17);

export function LogoMark({ size = 32, title }: { size?: number; title?: string }) {
  return (
    <svg width={size} height={size * 1.08} viewBox="0 0 100 108" role={title ? "img" : undefined} aria-hidden={title ? undefined : true} aria-label={title}>
      <path d={DROP_PATH} fill="none" stroke="currentColor" strokeWidth={5} strokeLinejoin="round" />
      <path d={FLOWER_PATH} fill="none" stroke="currentColor" strokeWidth={3.2} strokeLinejoin="round" />
    </svg>
  );
}

/** Mark + wordmarks. The Arabic and Latin names work together or apart. */
export function Logo({ locale, size = 28 }: { locale: string; size?: number }) {
  return (
    <span className="logo" dir="ltr">
      <LogoMark size={size} />
      <span className="logo-word logo-latin">RAHIQ</span>
      <span className="logo-word logo-arabic" lang="ar">رحيق</span>
      <span className="sr-only">{locale === "ar" ? "رحيق" : "Rahiq"}</span>
    </span>
  );
}
