// First-load JavaScript per route, Brotli-compressed, against a running server (performance.md; ADR-018).
//   node scripts/bundle-budget.mjs [baseUrl]
// Ceilings are regression guards set just above what the build ships today; the plan's targets and the gap are in ADR-018.
import { brotliCompressSync } from "node:zlib";

const base = (process.argv[2] ?? process.env.E2E_BASE_URL ?? "http://localhost:3000").replace(/\/$/, "");
const budgets = [
  { route: "/tr", kb: 195 },
  { route: "/tr/honey", kb: 195 },
  { route: "/tr/p/kestane-bali", kb: 195 },
  { route: "/tr/checkout", kb: 172 },
];

const cache = new Map();
async function size(url) {
  if (!cache.has(url)) {
    const body = Buffer.from(await (await fetch(url)).arrayBuffer());
    cache.set(url, brotliCompressSync(body).length);
  }
  return cache.get(url);
}

let failed = false;
for (const { route, kb } of budgets) {
  const html = await (await fetch(base + route, { headers: { "accept-language": "tr" } })).text();
  // Only scripts the page loads up front; dynamic imports (3D, GSAP) are fetched later and do not count here.
  const srcs = [...html.matchAll(/<script[^>]+src="([^"]+\.js)"/g)].map((m) => new URL(m[1], base).href);
  const total = (await Promise.all([...new Set(srcs)].map(size))).reduce((a, b) => a + b, 0) / 1024;
  const ok = total <= kb;
  failed ||= !ok;
  console.log(`${ok ? "ok  " : "FAIL"} ${route.padEnd(22)} ${total.toFixed(1).padStart(6)} KB br  (ceiling ${kb} KB, ${srcs.length} scripts)`);
}
process.exit(failed ? 1 : 0);
