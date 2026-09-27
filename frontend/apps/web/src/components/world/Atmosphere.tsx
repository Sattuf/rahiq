import "server-only";
import { existsSync } from "node:fs";
import { join } from "node:path";
import Image from "next/image";
import type { CSSProperties } from "react";

/**
 * Atmosphere pictures (brand-identity.md §8: generated imagery is for backgrounds and banners only, never a product).
 * Each has a ~500-byte blurred preview inlined in the HTML: it paints with the first byte, so the colours of the
 * plains or the bottles are there before any image request. The full picture is served through next/image
 * (AVIF/WebP, sized per screen) once its file is in public/atmosphere/<name>.jpg; until then the preview alone stays.
 */
const pictures = {
  "honey-plains": { blur: "data:image/webp;base64,UklGRlgBAABXRUJQVlA4IEwBAADQBwCdASooABcAPsFSo0ynpKMiKrgMAPAYCWgAtQjCPPMrI1kuICBvpJyzekWYKjv944TA/Tqb4fmhXc++PS9V0ZZt/KZ7YAD+6vMUHLgFEvx1sUtrE12DorozHr6Lw280qJrDRzBVkCFP3Ci78UcNZxZCqpFEC4bzArKJZrdGOyYqvbUxUkJFJVRiUKeFi8COClBRo637ELlao7++a1VD9zpimUKcC8if7z/7LltoOMGBQHOuZfeZ08SNUso5AiF0nt2EHTkcvwrSjA24srqfcl3efJBf5XZAFP3vxb9mZmQyUdVW0haLujV6oDyIW5Wu9ztNkpiuBxJqLrjrf4J3UAqTyTHnrF3IYgqX47dt4NEWFnrSZWyTy7gAUFlJRcZm3RysAGL/0X2MB+/RnPgWBxnF5vhO5tgg4e/NWK8WaqQCwQMt+beepQAAAA==" },
  "perfume-bottles": { blur: "data:image/webp;base64,UklGRiQBAABXRUJQVlA4IBgBAACQBgCdASooABcAPr1Mn0qnJCKhsAqo4BeJZADFCoM6A0digJIjirUtOFvR9/QAHlNTJJN6gXhSIs9MfIXAAP78VlK0CJc1joJ06OCskIueSEkb8i36hLQU5Kz2oTegwGTBF24DCaew2gOEOhomNpgxo8/H8uIZFScQQjXwX4Z/n0ZUzP6PO/wBSxh+4ndz128kUU+aqrMJaX/8sGnyYmMOjgY/ucg1YB7bieWf2C7s6UyYs2inI3TorN5L4iqXi4GN/kNL83p7qtmKhNcgJQp8nv1y4NAsK/einm5JFZk7dKhHcjJJ0mcXy7VZm+NUCpHEPKUjR2H8YxWHdodYvt0WaL+7ZpNCq9dyWLUIZkbLBssZavLjAAAA" },
  "honey-jar": { blur: "data:image/webp;base64,UklGRlICAABXRUJQVlA4IEYCAADQDACdASooADIAPr1OoEqnJCMhsBqsAOAXiWYAtvtMDDSMbA2wI4dmya6Fhjw90GtFzupNdgCa5oObd19QRUZrcbSFRQdLlN0iuX3CWPRTnTXUo+tv0uWuNfuc1n6w2BHEeGE7coJYsuPr+px9NmAA/McBNGb1UZaOffctHA+9YND5HOauFU9pKfvDNeFyuwWnbY53yGAx9VFAeD9Wfa+lzimY7BcfC/660dRF95Zf1210FegXtvlYH2bPDjyiDNH7IMDyYq3VCb4JAJ+/U5XjqVOcKZYBvYPUZDRQguPTDMlLzYcP0ezZVbfUKrDbygbRFb96XtifQOJ7Mkfk4Cj4ANW7f0pKsrDxorZNjA9JwAZAawPWxKEa9mjEtVkFzU5WSCTHvxP7LaneInSCLC74xahNcJdCXCjkmIp2O4/cB07hCUaugMqKDe50vkN4zUanGrafWuaHHy+dKdyH1I1ySGbY0WqzIIJ4kCu4Q8fbXfinyEI2APBBkqLUI7V1Ywwm0UCrlli7QjaoPWrjI6flI32D1vZ9whvFMh0L+I+orrAnRevyT8nSTPRSmGoSnTg60ryAN3KQrjesjbFLILPgIT+oaOV1u2TQU2s1r/j1I/RHKAPUvMcgArg8l7lmfS0Pft1Sa72kM882zaoQQGUv3Au2tIe6NKQ6kJkqvsBJDVFax5I/MPVSHgudBmZCU8haIRulBmOGTBjr7VqcIwzaQlUyCV7Hnc+XfvlISS9WPaB/vJH50k7X1lQ9luUHbHtW6/KgAAA=" },
  "perfume-spray": { blur: "data:image/webp;base64,UklGRn4CAABXRUJQVlA4IHICAABQDACdASooADIAPsFWoU0npKMiKBqtUPAYCUAVyei/68gGtrPo91cO1NfzBF4eyxQBJ8H2iW0gfHp+c/9dc+/B1eJjDaDvwFundefYEEL0sLmpkwP9IWLSKaGSVB62apnyPlV/LS9FkcEAAAD+/QGbo5azb7douHFV/AFfwPHOR1QO6y3LePilPH0+VX9TQiCrFezfOjlHZXgC6tH0qxwQkdc0rcpxrXHILIiyFHHgi466Jpn4FOLzv8UYNjATYKDuJo3uYPcLNdil+tCYYDmWm+xUSZKPRqrdO+ShnXbLH5MetDLa3ZhNh9CSARpJc+fdXPtZ+y2N5JV5WjuKcT/upiY4tAzf0Xojc7CkO3kCeR3sY3o3qKIzdvDnmoA6c1c7y40yRN3CEngF8X1mAkNxB+tzNOXrZOWOyf64X9iPFYnK9ghuscstdVCR4vvsfWXupTH3w0ccR/1fihKsHckoo92GWC7EH7NIXZT4pGuTJ58MOxlStiv/zMdb9O4pXjF0SfZ7UO7e2e2uNxKrhsmgUXeEHQCUmDj7l97GO7OODzw+xyRLCtbPbdtQnOseehfsSh5KG3cyL9YhhqGY9mApKtFO6onLp4D07LWN3ggue01oEoPwKfN+viYWIWnlNc+Ou/i8rN+PyaSIRSdL4SvK9wFHUG+ZlVqyrMDFm/Vu+ohKkLIdzq7qrpnjEmXqIkLqHh44roJ3alFG9tQsr/lnSTx3JvI5oD3eDRvpH8HbWzFBkdFblSzRuUUT0Vos9ce3wGwDgChwqQsFROrfCweN9CSEo4yXIIb+TNQSafI4V8vBC6nhJDjVkzZW5ioisJ/4AA==" },
} as const;

export type AtmosphereName = keyof typeof pictures;

const present = new Map<AtmosphereName, string | null>();
function fileFor(name: AtmosphereName): string | null {
  if (!present.has(name)) {
    const found = ["jpg", "webp", "png"].map((ext) => `/atmosphere/${name}.${ext}`).find((src) => existsSync(join(process.cwd(), "public", src)));
    present.set(name, found ?? null);
  }
  return present.get(name) ?? null;
}

export function Atmosphere({ name, sizes, priority, className = "" }: { name: AtmosphereName; sizes: string; priority?: boolean; className?: string }) {
  const picture = pictures[name];
  const src = fileFor(name);
  return (
    <div className={`atmosphere ${className}`} aria-hidden="true" style={{ "--preview": `url(${picture.blur})` } as CSSProperties}>
      {src && <Image src={src} alt="" fill sizes={sizes} priority={priority} quality={70} style={{ objectFit: "cover" }} />}
    </div>
  );
}
