"use client";

import { useEffect } from "react";
import { useReducedMotion } from "@/lib/client/capability";

type IdleWindow = Window & { requestIdleCallback?: (cb: () => void, options?: { timeout: number }) => number; cancelIdleCallback?: (id: number) => void };

/**
 * Lenis smooth scrolling on the store only; off under reduced motion, and never on checkout (Law 10).
 * Not on touch screens at all (touch scrolling stays native there, so Lenis would only cost start-up time), and
 * elsewhere loaded when the browser is idle, after first paint and hydration.
 */
export function SmoothScroll() {
  const reduced = useReducedMotion();

  useEffect(() => {
    if (reduced || window.matchMedia("(pointer: coarse)").matches) return;
    let frame = 0;
    let cancelled = false;
    let destroy = () => {};
    const start = () =>
      void Promise.all([import("lenis"), import("gsap"), import("gsap/ScrollTrigger")]).then(([{ default: Lenis }, { gsap }, { ScrollTrigger }]) => {
        if (cancelled) return;
        gsap.registerPlugin(ScrollTrigger);
        const lenis = new Lenis({ lerp: 0.12, smoothWheel: true });
        lenis.on("scroll", ScrollTrigger.update);
        const raf = (time: number) => {
          lenis.raf(time);
          frame = requestAnimationFrame(raf);
        };
        frame = requestAnimationFrame(raf);
        destroy = () => lenis.destroy();
      });

    const w = window as IdleWindow;
    const idle = w.requestIdleCallback ? w.requestIdleCallback(start, { timeout: 2000 }) : window.setTimeout(start, 600);
    return () => {
      cancelled = true;
      if (w.cancelIdleCallback) w.cancelIdleCallback(idle);
      else window.clearTimeout(idle);
      cancelAnimationFrame(frame);
      destroy();
    };
  }, [reduced]);

  return null;
}
