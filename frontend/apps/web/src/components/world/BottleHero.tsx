"use client";

import dynamic from "next/dynamic";
import { useDeviceTier } from "@/lib/client/capability";

// three.js, fiber and drei load only here, only on capable devices, only after hydration (Law 11, §4 budgets).
const BottleScene = dynamic(() => import("@rahiq/three/bottle"), { ssr: false, loading: () => null });

export function BottleHero() {
  const tier = useDeviceTier();
  if (tier !== "full") return null;
  return <BottleScene className="bottle-hero" liquid="#6B3E2A" />;
}
