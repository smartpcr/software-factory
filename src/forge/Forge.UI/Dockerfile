# Phase 7.7 — Dockerfile for the Forge.UI bundle.
# Build the static assets with Vite, then serve them with nginx.
# In the docker-compose stack the reverse proxy (caddy) terminates TLS and
# routes /api → forge-api, everything else → this container.

FROM node:20-alpine AS build
WORKDIR /app
COPY src/forge/Forge.UI/package.json src/forge/Forge.UI/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/forge/Forge.UI/ ./
RUN npm run build

FROM nginx:1.27-alpine AS runtime
COPY --from=build /app/dist /usr/share/nginx/html
# SPA fallback so client-side routes (e.g. /audit) don't 404 on direct hits.
COPY src/forge/Forge.UI/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 5181
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD wget --quiet --spider http://localhost:5181/ || exit 1
