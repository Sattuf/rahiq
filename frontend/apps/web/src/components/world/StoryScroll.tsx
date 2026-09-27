"use client";

import { type ReactNode, useEffect, useRef } from "react";
import { useReducedMotion } from "@/lib/client/capability";

/**
 * The brand story in one scroll-linked section: the bee's morning line, the perfumer's evening line, and where they
 * meet: the rose, which opens beside them as the section scrolls in (RoseBloom, CSS only). Arabic lines reveal as whole
 * lines behind a mask, never letter by letter (frontend-experience.md §6). The rose and whatever follows the lines
 * are rendered on the server and passed in, so they add nothing to this component's JavaScript.
 */
export function StoryScroll({ title, lines, visual, children }: { title: string; lines: string[]; visual?: ReactNode; children?: ReactNode }) {
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
      <div className="container story-grid">
        {visual && <div className="story-visual">{visual}</div>}
        <div className="story-inner">
          <h2 className="story-title">{title}</h2>
          {lines.map((line, i) => (
            <p key={line} className={`story-line story-line-${i}`}>
              {line}
            </p>
          ))}
        </div>
      </div>
      {children}
    </section>
  );
}
