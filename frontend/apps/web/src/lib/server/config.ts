import "server-only";

/** Server-only settings. The API URL is internal (container network); the browser only ever talks to /api/bff. */
export const config = {
  apiUrl: (process.env.API_INTERNAL_URL ?? "http://localhost:5080").replace(/\/$/, ""),
  sessionSecret: process.env.SESSION_SECRET ?? "development-session-secret-change-me-0123456789",
  revalidateSecret: process.env.REVALIDATE_SECRET ?? "dev-revalidate-secret",
  secureCookies: process.env.NODE_ENV === "production",
  siteUrl: (process.env.NEXT_PUBLIC_SITE_URL ?? "http://localhost:3000").replace(/\/$/, ""),
  seller: {
    name: process.env.SELLER_NAME ?? "[Şirket unvanı]",
    address: process.env.SELLER_ADDRESS ?? "[Açık adres]",
    mersis: process.env.SELLER_MERSIS ?? "[MERSİS no]",
    etbis: process.env.SELLER_ETBIS ?? "[ETBİS no]",
    email: process.env.SELLER_EMAIL ?? "destek@rahiq.example",
    phone: process.env.SELLER_PHONE ?? "[Telefon]",
  },
};

// Guard the running server, not the build (next build also runs with NODE_ENV=production).
if (process.env.NODE_ENV === "production" && process.env.NEXT_PHASE !== "phase-production-build" && config.sessionSecret.startsWith("development")) {
  throw new Error("SESSION_SECRET must be set in production.");
}
