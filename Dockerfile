FROM node:22-bookworm-slim AS client
WORKDIR /client
COPY src/Loomi/Client/package*.json ./
RUN npm ci --no-audit --no-fund
COPY src/Loomi/Client/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /source
COPY NuGet.Config ./
COPY src/Loomi/Loomi.csproj src/Loomi/
RUN dotnet restore src/Loomi/Loomi.csproj --configfile NuGet.Config
COPY src/Loomi/ src/Loomi/
COPY --from=client /wwwroot/dist/ src/Loomi/wwwroot/dist/
RUN dotnet publish src/Loomi/Loomi.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
# Bumping the revision rebuilds the runtime stage from scratch. On Harbora that is the way past a cached layer the server can no longer restore:
# removing an older build image breaks the cache chain of the images built on it.
LABEL org.opencontainers.image.title="Loomi" org.opencontainers.image.source="https://github.com/sadrazkh/Loomi" org.opencontainers.image.revision="2"
USER root
RUN apt-get update && apt-get install -y --no-install-recommends tini xvfb x11vnc novnc websockify fluxbox python3 curl ca-certificates sqlite3 \
    libnss3 libnspr4 libatk1.0-0 libatk-bridge2.0-0 libatspi2.0-0 libx11-6 libxcomposite1 libxdamage1 libxext6 libxfixes3 libxrandr2 libgbm1 libdrm2 libxcb1 libxkbcommon0 libasound2t64 libcups2 libdbus-1-3 libpango-1.0-0 libcairo2 fonts-liberation fonts-noto-core \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/ ./
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright DISPLAY=:99 ASPNETCORE_URLS=http://+:8080 Storage__Root=/data
RUN ./\.playwright/node/linux-x64/node ./\.playwright/package/cli.js install chromium \
    && chmod -R a+rX /ms-playwright && mkdir -p /data/profiles /data/images /data/keys && chown -R app:app /data /app
COPY deploy/entrypoint.sh /entrypoint.sh
# An upload from Windows can carry CRLF and loses the executable bit on the way, so neither is left to the source tree.
RUN sed -i 's/\r$//' /entrypoint.sh && chmod 755 /entrypoint.sh
USER app
EXPOSE 8080
# No Host header, which HTTP/1.0 allows and the host filter lets through: the check must not depend on AllowedHosts. With the name "localhost" it
# failed wherever AllowedHosts named only the public domain, as on Harbora, and the container read "unhealthy" while serving perfectly.
HEALTHCHECK --interval=30s --timeout=5s CMD curl -fsS --http1.0 -H "Host:" http://127.0.0.1:8080/health || exit 1
# tini reaps what Chromium orphans and passes the stop signal on; Harbora starts the container with no init of its own. -s keeps it
# working under Compose's init: true as well, where it is not PID 1.
ENTRYPOINT ["/usr/bin/tini", "-s", "--", "/entrypoint.sh"]
