"use client";

import { useSyncExternalStore } from "react";

const REDUCED = "(prefers-reduced-motion: reduce)";

function subscribe(callback: () => void) {
  const query = window.matchMedia(REDUCED);
  query.addEventListener("change", callback);
  return () => query.removeEventListener("change", callback);
}

/**
 * Reduced motion, read before anything animates and followed live (frontend-experience.md §5, level 3).
 * On the server (and before hydration) it answers "reduced": calm until we know.
 */
export function useReducedMotion(): boolean {
  return useSyncExternalStore(subscribe, () => window.matchMedia(REDUCED).matches, () => true);
}

export type Tier = "full" | "light" | "static";

let cachedTier: Exclude<Tier, "static"> | null = null;

function capabilityTier(): Exclude<Tier, "static"> {
  if (cachedTier) return cachedTier;
  const nav = navigator as Navigator & { deviceMemory?: number; connection?: { saveData?: boolean } };
  const webgl2 = !!document.createElement("canvas").getContext("webgl2");
  const strong = (nav.hardwareConcurrency ?? 2) >= 6 && (nav.deviceMemory ?? 4) >= 4 && !nav.connection?.saveData;
  const wide = window.matchMedia("(min-width: 900px)").matches;
  cachedTier = webgl2 && strong && wide ? "full" : "light";
  return cachedTier;
}

/**
 * The degradation ladder (frontend-experience.md §5): full WebGL on capable devices, light effects on weak ones,
 * nothing moving under reduced motion. The expensive transmission material only runs on "full".
 */
export function useDeviceTier(): Tier {
  return useSyncExternalStore(subscribe, () => (window.matchMedia(REDUCED).matches ? "static" : capabilityTier()), () => "static");
}
