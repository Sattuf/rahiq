"use client";

import { useEffect, useRef } from "react";
import { useReducedMotion } from "@/lib/client/capability";

/**
 * The slow pour (frontend-experience.md §3.4): a thread of honey runs from the spoon as you scroll down and fills
 * the jar; scrolling up draws it back. The shown value lags the scroll and catches up (a lerp), like real viscosity,
 * and the loop stops when it settles. Reduced motion: the full jar, no movement.
 */
export function HoneyPour({ caption }: { caption: string }) {
  const root = useRef<HTMLElement>(null);
  const reduced = useReducedMotion();

  useEffect(() => {
    const el = root.current;
    if (!el) return;
    if (reduced) {
      el.style.setProperty("--pour", "1");
      return;
    }

    let target = 0;
    let shown = 0;
    let frame = 0;
    const settle = () => {
      shown += (target - shown) * 0.07; // Viscosity: the honey is always a little behind the scroll.
      el.style.setProperty("--pour", shown.toFixed(4));
      frame = Math.abs(target - shown) > 0.0005 ? requestAnimationFrame(settle) : 0;
    };
    const onScroll = () => {
      const rect = el.getBoundingClientRect();
      const travel = rect.height - window.innerHeight;
      target = Math.min(1, Math.max(0, -rect.top / Math.max(1, travel)));
      if (!frame) frame = requestAnimationFrame(settle);
    };

    onScroll();
    window.addEventListener("scroll", onScroll, { passive: true });
    return () => {
      window.removeEventListener("scroll", onScroll);
      cancelAnimationFrame(frame);
    };
  }, [reduced]);

  return (
    <section ref={root} className="pour" aria-label={caption}>
      <div className="pour-stage">
        <svg className="pour-svg" viewBox="0 0 300 520" aria-hidden="true">
          <defs>
            <clipPath id="jar-inside">
              <path d="M96 300c-14 10-20 24-20 42v112c0 16 12 28 28 28h92c16 0 28-12 28-28V342c0-18-6-32-20-42z" />
            </clipPath>
            <linearGradient id="honey-fill" x1="0" x2="0" y1="0" y2="1">
              <stop offset="0" stopColor="#E6C35C" />
              <stop offset="1" stopColor="#C07E24" />
            </linearGradient>
          </defs>
          {/* Spoon */}
          <path d="M40 60c30-20 80-24 112-10 10 4 10 14 0 18-32 14-82 10-112-8z" fill="#8A6A45" />
          <path d="M150 58 L276 30" stroke="#8A6A45" strokeWidth="9" strokeLinecap="round" />
          {/* Thread: grows with --pour */}
          <path className="pour-thread" d="M96 72 C98 150 104 220 150 300 C152 330 150 380 150 470" pathLength={1} fill="none" stroke="url(#honey-fill)" strokeWidth="7" strokeLinecap="round" />
          {/* Jar */}
          <g clipPath="url(#jar-inside)">
            <rect className="pour-fill" x="70" y="300" width="160" height="190" fill="url(#honey-fill)" />
          </g>
          <path d="M96 300c-14 10-20 24-20 42v112c0 16 12 28 28 28h92c16 0 28-12 28-28V342c0-18-6-32-20-42" fill="none" stroke="#5a3a0e" strokeWidth="3" />
          <rect x="92" y="284" width="116" height="18" rx="5" fill="none" stroke="#5a3a0e" strokeWidth="3" />
        </svg>
        <p className="pour-caption display">{caption}</p>
      </div>
    </section>
  );
}
