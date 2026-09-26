import { describe, expect, it } from "vitest";
import { contrast, tokens } from "@rahiq/ui";

// WCAG 2.2 AA: 4.5:1 for body text, 3:1 for large text and controls (brand-identity.md §10).
describe("palette contrast", () => {
  const { canvas, ink, accent, muted } = tokens.brand;

  it("body text on the canvas", () => {
    expect(contrast(ink, canvas)).toBeGreaterThanOrEqual(7);
    expect(contrast(muted, canvas)).toBeGreaterThanOrEqual(4.5);
  });

  // Secondary text also sits on each world's surface and on the tinted "picks" bands (Lighthouse caught 4.46 there).
  it("muted text stays AA on every surface", () => {
    for (const surface of [tokens.light.perfume.surface, tokens.light.honey.surface, "#E2E4E3", "#E4DED3"]) {
      expect(contrast(muted, surface), surface).toBeGreaterThanOrEqual(4.5);
    }
  });

  it("the accent carries white button text", () => {
    expect(contrast("#FFFFFF", accent)).toBeGreaterThanOrEqual(4.5);
  });

  it("perfume's primary is text-grade on its surface", () => {
    expect(contrast(tokens.light.perfume.primary, tokens.light.perfume.surface)).toBeGreaterThanOrEqual(4.5);
  });

  // Honey amber (#C07E24) is too light for text on cream; it is decoration only, and text/bars use primaryInk.
  it("honey text uses the ink shade, never the amber", () => {
    const { primary, primaryInk, surface } = tokens.light.honey;
    expect(contrast(primary, surface)).toBeLessThan(3);
    expect(contrast(primaryInk, surface)).toBeGreaterThanOrEqual(4.5);
    expect(contrast(primaryInk, canvas)).toBeGreaterThanOrEqual(4.5);
  });

  it("the ratio is symmetric and 21 at the extremes", () => {
    expect(contrast("#000000", "#FFFFFF")).toBeCloseTo(21, 5);
    expect(contrast("#FFFFFF", "#000000")).toBeCloseTo(21, 5);
  });
});
