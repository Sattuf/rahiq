"use client";

import { useEffect, useRef } from "react";
import { useReducedMotion } from "@/lib/client/capability";

type Tier = { key: "top" | "heart" | "base"; title: string; hint: string; notes: string[] };

/**
 * The perfume world's moment (frontend-experience.md §3.2): scrolling down descends through the layers of a
 * fragrance, from light top notes to deep base notes. Motion follows the scroll (scrubbed); every note is real
 * text in the DOM. With reduced motion the three layers are simply shown, final state.
 */
export function NotesJourney({ title, name, tiers, compact }: { title: string; name: string; tiers: Tier[]; compact?: boolean }) {
  const root = useRef<HTMLElement>(null);
  const reduced = useReducedMotion();

  useEffect(() => {
    if (reduced || !root.current || compact) return;
    let cleanup = () => {};
    void Promise.all([import("gsap"), import("gsap/ScrollTrigger")]).then(([{ gsap }, { ScrollTrigger }]) => {
      gsap.registerPlugin(ScrollTrigger);
      const ctx = gsap.context(() => {
        const timeline = gsap.timeline({
          scrollTrigger: { trigger: root.current, start: "top top", end: "+=180%", scrub: 0.8, pin: ".journey-stage" },
        });
        timeline
          .fromTo(".journey-depth", { yPercent: 0 }, { yPercent: -66.6, ease: "none" }, 0)
          .fromTo(".journey-light", { opacity: 0.9 }, { opacity: 0.25, ease: "none" }, 0);
        gsap.utils.toArray<HTMLElement>(".journey-note").forEach((note, i) => {
          gsap.to(note, { y: (i % 2 ? -1 : 1) * 14, repeat: -1, yoyo: true, duration: 3 + (i % 3), ease: "sine.inOut" });
        });
      }, root);
      cleanup = () => ctx.revert();
    });
    return () => cleanup();
  }, [reduced, compact]);

  return (
    <section ref={root} className={`journey${compact ? " journey-compact" : ""}`} aria-labelledby="journey-title">
      <div className="journey-stage">
        <div className="journey-light" aria-hidden="true" />
        <div className="container journey-head">
          <p className="eyebrow">{name}</p>
          <h2 id="journey-title">{title}</h2>
        </div>
        <div className="journey-window">
          <ol className="journey-depth">
            {tiers.map((tier) => (
              <li key={tier.key} className={`journey-tier journey-${tier.key}`}>
                <div className="container">
                  <h3>{tier.title}</h3>
                  <p className="muted">{tier.hint}</p>
                  <ul className="journey-notes">
                    {tier.notes.map((note) => (
                      <li key={note} className="journey-note">
                        {note}
                      </li>
                    ))}
                  </ul>
                </div>
              </li>
            ))}
          </ol>
        </div>
      </div>
    </section>
  );
}
