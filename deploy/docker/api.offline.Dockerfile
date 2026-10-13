# syntax=docker/dockerfile:1.7

ARG WEB_DEPS_IMAGE=hsjm-cache/web-deps:pnpm-10.26.2
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-noble
ARG RUNTIME_DEPS_IMAGE=hsjm-cache/runtime-deps:20260903

FROM ${WEB_DEPS_IMAGE} AS web-build
WORKDIR /src

COPY web/package.json web/pnpm-lock.yaml web/.npmrc ./
RUN pnpm install --offline --frozen-lockfile

COPY web/ ./
RUN pnpm --config.verify-deps-before-run=false build \
    && test -f dist/index.html

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
ENV NUGET_PACKAGES=/root/.nuget/packages

COPY deploy/docker/nuget.offline.config /tmp/nuget.offline.config
COPY api/ ./api/
RUN --mount=type=bind,from=nuget,target=/root/.nuget/packages,readonly \
    dotnet restore ./api/HSJM.AI/HSJM.AI.csproj \
      --configfile /tmp/nuget.offline.config \
      --packages /root/.nuget/packages
RUN --mount=type=bind,from=nuget,target=/root/.nuget/packages,readonly \
    dotnet publish ./api/HSJM.AI/HSJM.AI.csproj \
      --configuration Release \
      --no-restore \
      --output /out \
      /p:UseAppHost=false

FROM ${RUNTIME_DEPS_IMAGE} AS runtime

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    PATH=/opt/venv/bin:$PATH \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    TZ=Asia/Shanghai

USER root
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
