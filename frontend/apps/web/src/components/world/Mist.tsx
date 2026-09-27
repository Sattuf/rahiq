import type { CSSProperties } from "react";

/**
 * Perfume leaving the atomizer: a few soft puffs that spray out, widen and fade as the scent spreads.
 * CSS only (transform and opacity), no JavaScript; gone under reduced motion. `mode` picks when it plays:
 * "loop" sprays every few seconds (world hero), "hover" when the bottle is pointed at or ordered (product cards).
 */
export function Mist({ mode, count = 6 }: { mode: "loop" | "hover"; count?: number }) {
  return (
    <span className={`mist mist-${mode}`} aria-hidden="true">
      {Array.from({ length: count }, (_, i) => (
        <i key={i} style={{ "--p": i } as CSSProperties} />
      ))}
    </span>
  );
}
