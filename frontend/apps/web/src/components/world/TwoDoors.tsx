"use client";

import Link from "next/link";
import { useEffect, useRef } from "react";
import { useReducedMotion } from "@/lib/client/capability";
import { CausticsLayer } from "./CausticsLayer";

type Door = { title: string; text: string; cta: string; href: string };

/**
 * The home hero (frontend-experience.md §3.1): evening light on one side, morning light on the other. The pointer
 * (or tilting the phone) leans the light towards a side, which widens a little. Plain links underneath: it works
 * without JavaScript, without WebGL and with reduced motion. On phones the doors stack.
 */
export function TwoDoors({ perfume, honey, tagline, lede }: { perfume: Door; honey: Door; tagline: string; lede: string }) {
  const root = useRef<HTMLDivElement>(null);
  const reduced = useReducedMotion();

  useEffect(() => {
    const el = root.current;
    if (!el || reduced) return;
    let target = 0.5;
    let current = 0.5;
    let frame = 0;
    const rtl = document.documentElement.dir === "rtl";

    const tick = () => {
      current += (target - current) * 0.08;
      el.style.setProperty("--lean", current.toFixed(4));
      if (Math.abs(target - current) > 0.001) frame = requestAnimationFrame(tick);
    };
    const lean = (x: number) => {
      target = Math.min(0.62, Math.max(0.38, rtl ? 1 - x : x));
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(tick);
    };
    const onMove = (e: PointerEvent) => {
      const rect = el.getBoundingClientRect();
      lean((e.clientX - rect.left) / rect.width);
    };
    const onLeave = () => lean(0.5);
    const onTilt = (e: DeviceOrientationEvent) => {
      if (e.gamma !== null) lean(0.5 + Math.max(-30, Math.min(30, e.gamma)) / 120);
    };

    el.addEventListener("pointermove", onMove);
    el.addEventListener("pointerleave", onLeave);
    window.addEventListener("deviceorientation", onTilt);
    return () => {
      cancelAnimationFrame(frame);
      el.removeEventListener("pointermove", onMove);
      el.removeEventListener("pointerleave", onLeave);
      window.removeEventListener("deviceorientation", onTilt);
    };
  }, [reduced]);

  return (
    <section className="doors" ref={root} aria-label={tagline}>
      <Link href={perfume.href} className="door door-perfume" data-world="perfume">
        <CausticsLayer world="perfume" />
        <span className="door-content">
          <span className="eyebrow door-eyebrow">{perfume.title}</span>
          <span className="door-text">{perfume.text}</span>
          <span className="door-cta">{perfume.cta} →</span>
        </span>
      </Link>
      <div className="doors-center">
        <h1 className="doors-title">{tagline}</h1>
        <p className="doors-lede">{lede}</p>
      </div>
      <Link href={honey.href} className="door door-honey" data-world="honey">
        <CausticsLayer world="honey" />
        <span className="door-content">
          <span className="eyebrow door-eyebrow">{honey.title}</span>
          <span className="door-text">{honey.text}</span>
          <span className="door-cta">{honey.cta} →</span>
        </span>
      </Link>
    </section>
  );
}
