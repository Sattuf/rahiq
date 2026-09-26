import type { MetadataRoute } from "next";

const site = (process.env.NEXT_PUBLIC_SITE_URL ?? "http://localhost:3000").replace(/\/$/, "");

export default function robots(): MetadataRoute.Robots {
  return {
    rules: [{ userAgent: "*", allow: "/", disallow: ["/api/", "/*/admin", "/*/checkout", "/*/account", "/*/batch/"] }],
    sitemap: `${site}/sitemap.xml`,
  };
}
