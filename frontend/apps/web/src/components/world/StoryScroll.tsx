"use client";

import { useEffect, useRef } from "react";
import { useReducedMotion } from "@/lib/client/capability";

/**
 * The brand story in one scroll-linked section: the bee's morning line, the perfumer's evening line, and where they
 * meet. Arabic lines reveal as whole lines behind a mask, never letter by letter (frontend-experience.md §6).
 */
export function StoryScroll({ title, lines }: { title: string; lines: string[] }) {
  const root = useRef<HTMLElement>(null);
  const reduced = useReducedMotion();

  useEffect(() => {
    if (reduced || !root.current) return;
    let cleanup = () => {};
    void Promise.all([import("gsap"), import("gsap/ScrollTrigger")]).then(([{ gsap }, { ScrollTrigger }]) => {
      gsap.registerPlugin(ScrollTrigger);
      const ctx = gsap.context(() => {
        gsap.utils.toArray<HTMLElement>(".story-line").forEach((line) => {
          // Mask only, no fade: a half-transparent line would fail contrast while it is on screen.
          gsap.fromTo(line, { clipPath: "inset(0 0 100% 0)" }, {
            clipPath: "inset(0 0 0% 0)",
            duration: 0.9,
            ease: "power2.out",
            // Once revealed, a line stays: scrolling back up never hides text the reader has already seen.
            scrollTrigger: { trigger: line, start: "top 85%", once: true },
          });
        });
      }, root);
      cleanup = () => ctx.revert();
    });
    return () => cleanup();
  }, [reduced]);

  return (
    <section className="section story" ref={root}>
      <div className="container story-inner">
        <h2 className="story-title">{title}</h2>
        {lines.map((line, i) => (
          <p key={line} className={`story-line story-line-${i}`}>
            {line}
          </p>
        ))}
      </div>
    </section>
  );
}
