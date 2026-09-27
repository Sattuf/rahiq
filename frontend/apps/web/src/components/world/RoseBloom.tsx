import type { CSSProperties } from "react";

/**
 * The rose (brand-identity.md §1): the flower both rooms start from. Perfume is distilled from its petals, and the
 * bee's nectar and honey stand for every flower it visits. Drawn, not photographed (Law 8 covers products only).
 *
 * Plain SVG with no JavaScript: the petals open with a CSS animation on transform and opacity only, so it runs on
 * the compositor and never delays the first paint. "hero" opens on load, "scroll" opens as it scrolls into view
 * where the browser supports scroll-driven animations (elsewhere it is simply open). Reduced motion: open, still.
 */
export function RoseBloom({ variant = "hero", className = "" }: { variant?: "hero" | "scroll" | "still"; className?: string }) {
  // Gradient ids are per variant: each variant appears once on a page, and ids must not collide.
  const id = (name: string) => `rose-${name}-${variant}`;
  return (
    <svg className={`rose rose-${variant} ${className}`} viewBox="0 0 200 250" aria-hidden="true" focusable="false">
      <defs>
        <linearGradient id={id("back")} x1="0" x2="0" y1="0" y2="1">
          <stop offset="0" stopColor="#a4707e" />
          <stop offset="1" stopColor="#5e3440" />
        </linearGradient>
        <linearGradient id={id("bud")} x1="0" x2="0" y1="0" y2="1">
          <stop offset="0" stopColor="#8e5a68" />
          <stop offset="1" stopColor="#6a3c49" />
        </linearGradient>
        <linearGradient id={id("front")} x1="0" x2="0" y1="0" y2="1">
          <stop offset="0" stopColor="#d9adb6" />
          <stop offset="1" stopColor="#9c6674" />
        </linearGradient>
        <radialGradient id={id("heart")} cx="0.5" cy="0.6" r="0.6">
          <stop offset="0" stopColor="#e6c35c" />
          <stop offset="1" stopColor="#a8838c" stopOpacity="0" />
        </radialGradient>
      </defs>

      <g className="rose-stem">
        <path d="M100 138 C98 172 104 200 98 246" fill="none" stroke="#5b6636" strokeWidth="4" strokeLinecap="round" />
        <path className="rose-leaf rose-leaf-a" d="M100 184 C84 170 62 172 52 182 C66 194 88 196 100 184 Z" fill="#5b6636" />
        <path className="rose-leaf rose-leaf-b" d="M101 210 C116 196 138 198 148 208 C134 220 112 222 101 210 Z" fill="#6c7842" />
      </g>

      <g className="rose-head">
        <path className="petal" style={{ "--i": 0 } as CSSProperties} d="M100 134 C72 134 44 116 44 88 C44 74 54 66 64 70 C74 60 90 70 96 84 Z" fill={`url(#${id("back")})`} />
        <path className="petal" style={{ "--i": 1 } as CSSProperties} d="M100 134 C128 134 156 116 156 88 C156 74 146 66 136 70 C126 60 110 70 104 84 Z" fill={`url(#${id("back")})`} />
        <path className="petal" style={{ "--i": 2 } as CSSProperties} d="M68 88 C64 64 82 50 100 57 C118 50 136 64 132 88 C118 78 82 78 68 88 Z" fill={`url(#${id("back")})`} />
        <path className="petal" style={{ "--i": 3 } as CSSProperties} d="M78 92 C76 72 92 63 100 67 C108 63 124 72 122 92 C112 84 88 84 78 92 Z" fill={`url(#${id("bud")})`} />
        <ellipse className="petal rose-glow" style={{ "--i": 4 } as CSSProperties} cx="100" cy="84" rx="20" ry="14" fill={`url(#${id("heart")})`} />
        <path
          className="petal"
          style={{ "--i": 4 } as CSSProperties}
          d="M90 86 C89 74 110 72 111 82 C112 90 97 92 97 85 C97 80 104 79 105 83"
          fill="none"
          stroke="#5e3440"
          strokeWidth="2.2"
          strokeLinecap="round"
        />
        <path className="petal" style={{ "--i": 5 } as CSSProperties} d="M102 134 C78 132 60 116 62 94 C74 102 88 104 102 100 Z" fill={`url(#${id("front")})`} />
        <path className="petal" style={{ "--i": 5 } as CSSProperties} d="M98 134 C122 132 140 116 138 94 C126 102 112 104 98 100 Z" fill={`url(#${id("front")})`} />
        <path className="petal" style={{ "--i": 6 } as CSSProperties} d="M68 102 C72 126 88 138 100 138 C112 138 128 126 132 102 C120 110 110 112 100 108 C90 112 80 110 68 102 Z" fill={`url(#${id("front")})`} />
        <path className="petal" style={{ "--i": 6 } as CSSProperties} d="M68 102 C80 110 90 112 100 108 C110 112 120 110 132 102" fill="none" stroke="#f1dde1" strokeOpacity=".6" strokeWidth="1.6" strokeLinecap="round" />
        <path className="rose-leaf" d="M100 136 C92 140 84 148 76 152 C88 152 96 146 100 140 Z M100 136 C108 140 116 148 124 152 C112 152 104 146 100 140 Z" fill="#5b6636" />
      </g>
    </svg>
  );
}

/** A single loose petal for the hero's drifting layer; shape and colour come from CSS. */
export function Petals({ count = 6 }: { count?: number }) {
  return (
    <div className="petals" aria-hidden="true">
      {Array.from({ length: count }, (_, i) => (
        <span key={i} className="petal-fall" style={{ "--n": i } as CSSProperties} />
      ))}
    </div>
  );
}
