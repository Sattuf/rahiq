"use client";

import { tokens } from "@rahiq/ui";
import dynamic from "next/dynamic";
import { useEffect, useState } from "react";
import { useReducedMotion } from "@/lib/client/capability";

const Caustics = dynamic(() => import("@rahiq/three").then((m) => m.Caustics), { ssr: false });

/**
 * The brand's signature light (brand-identity.md §3). A static CSS glow is always there first (no WebGL, reduced
 * motion, before hydration); the shader is added after the page is interactive, so it never delays the LCP.
 */
export function CausticsLayer({ world }: { world: "perfume" | "honey" }) {
  const reduced = useReducedMotion();
  const [ready, setReady] = useState(false);
  const light = tokens.light[world];

  useEffect(() => {
    const idle = (window as Window & { requestIdleCallback?: (cb: () => void) => number }).requestIdleCallback;
    if (idle) idle(() => setReady(true));
    else setTimeout(() => setReady(true), 600);
  }, []);

  return (
    <div className="caustics-wrap" aria-hidden="true">
      <div className="caustics-fallback" />
      {ready && <Caustics className="caustics" tint={light.causticTint} intensity={light.causticIntensity} still={reduced} />}
    </div>
  );
}
