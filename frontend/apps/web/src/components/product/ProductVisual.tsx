import type { Media, Section } from "@rahiq/api-client";
import Image from "next/image";
import { ViewTransition } from "react";

/**
 * Real product photos only (Law 8). Until the shoot, an honest drawn silhouette with a note says the photo is coming,
 * rather than a generated picture pretending to be the product.
 * `morph` names the frame for a view transition: the same name on the card and on the product page makes the picture
 * travel from the grid into the gallery instead of cutting (globals.css, ".morph"). Each name appears once per page.
 */
export function ProductVisual({ media, section, name, note, priority, morph, sizes = "(max-width: 700px) 50vw, 25vw" }: {
  media?: Media | null;
  section: Section;
  name: string;
  note: string;
  priority?: boolean;
  morph?: string;
  sizes?: string;
}) {
  const frame = (
    <div className="product-frame" data-world={section === "shared" ? undefined : section}>
      {media ? (
        <Image src={media.url} alt={media.alt ?? name} fill sizes={sizes} priority={priority} style={{ objectFit: "cover" }} />
      ) : (
        <div className="placeholder" role="img" aria-label={`${name}. ${note}`}>
          {section === "perfume" ? <BottleSilhouette /> : section === "honey" ? <JarSilhouette /> : <BoxSilhouette />}
          <span className="placeholder-note" aria-hidden="true">
            {note}
          </span>
        </div>
      )}
    </div>
  );
  if (!morph) return frame;
  return (
    <ViewTransition name={morph} share="morph" default="none">
      {frame}
    </ViewTransition>
  );
}

function BottleSilhouette() {
  return (
    <svg viewBox="0 0 100 160" fill="none" stroke="currentColor" strokeWidth="2.2" aria-hidden="true">
      <rect x="38" y="6" width="24" height="18" rx="3" />
      <path d="M42 24v10c-16 6-26 14-26 30v72c0 8 6 14 14 14h40c8 0 14-6 14-14V64c0-16-10-24-26-30V24" />
      <path d="M24 92c16 6 36 6 52 0" opacity=".6" />
    </svg>
  );
}

function JarSilhouette() {
  return (
    <svg viewBox="0 0 100 130" fill="none" stroke="currentColor" strokeWidth="2.2" aria-hidden="true">
      <rect x="22" y="6" width="56" height="16" rx="4" />
      <path d="M26 22c-8 6-12 14-12 26v58c0 9 7 16 16 16h40c9 0 16-7 16-16V48c0-12-4-20-12-26" />
      <path d="M18 62c20 8 44 8 64 0" opacity=".6" />
      <path d="M40 40l6 6-6 6-6-6z M54 52l6 6-6 6-6-6z" opacity=".45" />
    </svg>
  );
}

function BoxSilhouette() {
  return (
    <svg viewBox="0 0 120 110" fill="none" stroke="currentColor" strokeWidth="2.2" aria-hidden="true">
      <path d="M10 36h100v64H10z M10 36l14-20h72l14 20" />
      <path d="M58 36v64 M62 36v64" opacity=".6" />
      <path d="M60 36c-10-14-24-14-24-4s14 4 24 4c10 0 24 6 24-4s-14-10-24 4" />
    </svg>
  );
}
