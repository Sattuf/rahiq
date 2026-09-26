import type { NextConfig } from "next";

const apiMedia = new URL(process.env.NEXT_PUBLIC_MEDIA_ORIGIN ?? "http://localhost:5080");

const nextConfig: NextConfig = {
  output: "standalone",
  poweredByHeader: false,
  reactStrictMode: true,
  transpilePackages: ["@rahiq/ui", "@rahiq/three", "@rahiq/i18n", "@rahiq/api-client"],
  images: {
    formats: ["image/avif", "image/webp"],
    remotePatterns: [
      { protocol: apiMedia.protocol.replace(":", "") as "http" | "https", hostname: apiMedia.hostname, port: apiMedia.port, pathname: "/**" },
    ],
  },
  async headers() {
    return [
      {
        source: "/:path*",
        headers: [
          { key: "X-Content-Type-Options", value: "nosniff" },
          { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
          { key: "Permissions-Policy", value: "camera=(), microphone=(), geolocation=(), payment=(self)" },
          { key: "Strict-Transport-Security", value: "max-age=31536000; includeSubDomains" },
        ],
      },
    ];
  },
};

export default nextConfig;
