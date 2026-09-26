# syntax=docker/dockerfile:1.7
# Rahiq storefront + admin (Next.js standalone). Build context = frontend/.
#   docker build -f infra/docker/web.Dockerfile --build-arg NEXT_PUBLIC_MEDIA_ORIGIN=https://api.rahiq.example -t rahiq-web:<sha> frontend
FROM node:22-alpine AS base
RUN corepack enable
WORKDIR /repo

FROM base AS deps
COPY pnpm-lock.yaml pnpm-workspace.yaml package.json ./
COPY apps/web/package.json apps/web/
COPY packages/ui/package.json packages/ui/
COPY packages/three/package.json packages/three/
COPY packages/i18n/package.json packages/i18n/
COPY packages/api-client/package.json packages/api-client/
RUN --mount=type=cache,id=pnpm,target=/root/.local/share/pnpm/store pnpm install --frozen-lockfile

FROM deps AS build
COPY . .
# Baked into the image: next/image allow-list and the public URLs used in metadata.
ARG NEXT_PUBLIC_MEDIA_ORIGIN=https://api.rahiq.example
ARG NEXT_PUBLIC_SITE_URL=https://rahiq.example
ARG NEXT_PUBLIC_API_ORIGIN=https://api.rahiq.example
ENV NEXT_PUBLIC_MEDIA_ORIGIN=$NEXT_PUBLIC_MEDIA_ORIGIN NEXT_PUBLIC_SITE_URL=$NEXT_PUBLIC_SITE_URL NEXT_PUBLIC_API_ORIGIN=$NEXT_PUBLIC_API_ORIGIN NEXT_TELEMETRY_DISABLED=1
RUN pnpm --filter @rahiq/web build

FROM node:22-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production NEXT_TELEMETRY_DISABLED=1 PORT=3000 HOSTNAME=0.0.0.0
COPY --from=build --chown=node:node /repo/apps/web/.next/standalone ./
COPY --from=build --chown=node:node /repo/apps/web/.next/static ./apps/web/.next/static
COPY --from=build --chown=node:node /repo/apps/web/public ./apps/web/public
USER node
EXPOSE 3000
HEALTHCHECK --interval=10s --timeout=3s --start-period=15s --retries=5 CMD wget -qO- http://127.0.0.1:3000/robots.txt >/dev/null || exit 1
CMD ["node", "apps/web/server.js"]
