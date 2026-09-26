/** Brand tokens (brand-identity.md §10). One brand, two lights: components never know which room they are in. */
export const tokens = {
  brand: { canvas: "#ECE9E3", ink: "#2A1F17", accent: "#8A5412", muted: "#625950" },
  light: {
    perfume: { primary: "#343858", surface: "#D6DEE2", secondary: "#A8838C", causticTint: "#B9C6F0", causticIntensity: 0.35 },
    honey: { primary: "#C07E24", primaryInk: "#5A3A0E", surface: "#F1EADB", secondary: "#5B6636", causticTint: "#FFC766", causticIntensity: 0.45 },
  },
  radius: { product: 4, control: 10, pill: 999 },
  motion: { ease: "cubic-bezier(0.22, 1, 0.36, 1)", fast: 180, base: 420, slow: 900 },
} as const;

export type World = "home" | "perfume" | "honey";

/** WCAG relative-luminance contrast ratio, used by the brand-package tests to prove the palette. */
export function contrast(foreground: string, background: string): number {
  const lum = (hex: string) => {
    const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255).map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
  };
  const [a, b] = [lum(foreground), lum(background)].sort((x, y) => y - x);
  return (a + 0.05) / (b + 0.05);
}
