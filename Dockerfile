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
USER root
RUN apt-get update && apt-get install -y --no-install-recommends xvfb x11vnc novnc websockify fluxbox python3 curl ca-certificates \
    libnss3 libnspr4 libatk1.0-0 libatk-bridge2.0-0 libatspi2.0-0 libx11-6 libxcomposite1 libxdamage1 libxext6 libxfixes3 libxrandr2 libgbm1 libdrm2 libxcb1 libxkbcommon0 libasound2t64 libcups2 libdbus-1-3 libpango-1.0-0 libcairo2 fonts-liberation fonts-noto-core \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/ ./
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright DISPLAY=:99 ASPNETCORE_URLS=http://+:8080 Storage__Root=/data
RUN ./\.playwright/node/linux-x64/node ./\.playwright/package/cli.js install chromium \
    && chmod -R a+rX /ms-playwright && mkdir -p /data/profiles /data/images /data/keys && chown -R app:app /data /app
COPY deploy/entrypoint.sh /entrypoint.sh
RUN chmod 755 /entrypoint.sh
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["/entrypoint.sh"]
