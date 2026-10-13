# syntax=docker/dockerfile:1.7

FROM node:22-alpine AS web-build
WORKDIR /src

ENV CI=true
RUN corepack enable && corepack prepare pnpm@10.26.2 --activate

COPY web/package.json web/pnpm-lock.yaml web/.npmrc ./
RUN --mount=type=cache,id=hsjm-pnpm-store,target=/pnpm/store \
    pnpm config set store-dir /pnpm/store \
    && pnpm install --frozen-lockfile

COPY web/ ./
RUN pnpm --config.verify-deps-before-run=false build \
    && test -f dist/index.html

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY api/ ./api/
RUN --mount=type=cache,id=hsjm-nuget-packages,target=/root/.nuget/packages \
    dotnet restore ./api/HSJM.AI/HSJM.AI.csproj \
      --configfile ./api/HSJM.AI/nuget.config
RUN --mount=type=cache,id=hsjm-nuget-packages,target=/root/.nuget/packages \
    dotnet publish ./api/HSJM.AI/HSJM.AI.csproj \
      --configuration Release \
      --no-restore \
      --output /out \
      /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    PATH=/opt/venv/bin:$PATH \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    TZ=Asia/Shanghai

USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       bash \
       ca-certificates \
       curl \
       fontconfig \
       fonts-noto-cjk \
       libgdiplus \
       python3 \
       python3-pip \
       python3-venv \
       tzdata \
       unzip \
    && rm -rf /var/lib/apt/lists/*

RUN --mount=type=cache,id=hsjm-pip-cache,target=/root/.cache/pip \
    python3 -m venv /opt/venv \
    && /opt/venv/bin/pip install --upgrade pip setuptools wheel

COPY api/HSJM.AI/wwwroot/skills/plc-analyzer/scripts/requirements.txt /tmp/requirements/plc-analyzer.txt
COPY api/HSJM.AI/wwwroot/skills/pdf-reader/scripts/requirements.txt /tmp/requirements/pdf-reader.txt
COPY api/HSJM.AI/wwwroot/skills/python-sandbox/scripts/requirements.txt /tmp/requirements/python-sandbox.txt
COPY api/HSJM.AI/wwwroot/skills/web-data-search/scripts/requirements.txt /tmp/requirements/web-data-search.txt
COPY api/HSJM.AI/wwwroot/skills/web-browser/requirements.txt /tmp/requirements/web-browser.txt
RUN --mount=type=cache,id=hsjm-pip-cache,target=/root/.cache/pip \
    /opt/venv/bin/pip install \
      -r /tmp/requirements/plc-analyzer.txt \
      -r /tmp/requirements/pdf-reader.txt \
      -r /tmp/requirements/python-sandbox.txt \
      -r /tmp/requirements/web-data-search.txt \
      -r /tmp/requirements/web-browser.txt \
    && /opt/venv/bin/python -m playwright install --with-deps chromium \
    && rm -rf /tmp/requirements /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build --chown=app:app /out/ /app/
RUN install -d -o app -g app \
       /opt/hsjm \
       /app/logs \
       /app/Uploads \
       /app/ParserTemplates \
       /app/GeneratedParsers \
    && if [ -d /app/wwwroot ]; then mv /app/wwwroot /opt/hsjm/wwwroot.seed; fi \
    && install -d -o app -g app /app/wwwroot

COPY --from=web-build --chown=app:app /src/dist/ /opt/hsjm/web.seed/
COPY --chmod=0755 --chown=app:app deploy/docker/api/entrypoint.sh /usr/local/bin/hsjm-entrypoint

USER app
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=60s --retries=8 \
  CMD curl --fail --silent --show-error http://127.0.0.1:8080/health >/dev/null || exit 1

ENTRYPOINT ["/usr/local/bin/hsjm-entrypoint"]
