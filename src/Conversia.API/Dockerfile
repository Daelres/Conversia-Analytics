# syntax=docker/dockerfile:1
# Build context is the repository root:
#   docker build -f src/Conversia.API/Dockerfile -t conversia-api .
#   docker build -f src/Conversia.API/Dockerfile --target migrator -t conversia-migrator .
# Optional build args (scripts/docker-build.sh sets them from git): APP_VERSION, GIT_SHA, BUILD_DATE, SOURCE_URL.
#
# Stages: restore -> publish -> final (the API, default target)
#                 -> bundle -> migrator (one-shot EF Core migrations bundle)

# ---------------------------------------------------------------- restore
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

# Copy only the project files first so the restore layer is cached until a
# package reference or project reference changes.
COPY src/Conversia.Domain/Conversia.Domain.csproj src/Conversia.Domain/
COPY src/Conversia.Application/Conversia.Application.csproj src/Conversia.Application/
COPY src/Conversia.Infrastructure/Conversia.Infrastructure.csproj src/Conversia.Infrastructure/
COPY src/Conversia.API/Conversia.API.csproj src/Conversia.API/
RUN dotnet restore src/Conversia.API/Conversia.API.csproj

COPY src/Conversia.Domain/ src/Conversia.Domain/
COPY src/Conversia.Application/ src/Conversia.Application/
COPY src/Conversia.Infrastructure/ src/Conversia.Infrastructure/
COPY src/Conversia.API/ src/Conversia.API/

# --------------------------------------------------------------- publish
FROM restore AS publish
RUN dotnet publish src/Conversia.API/Conversia.API.csproj \
    -c Release --no-restore -o /app/publish /p:UseAppHost=false

# ---------------------------------------------------------------- bundle
# Builds a self-contained EF Core migrations bundle (single executable that
# applies the migrations in src/Conversia.Infrastructure/Persistence/Migrations).
FROM restore AS bundle
# BuildKit sets TARGETARCH (amd64 or arm64) to the platform the image is built
# for. The bundle is self-contained, so its runtime identifier must match the
# architecture of the migrator image or the executable cannot start.
ARG TARGETARCH
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    PATH="${PATH}:/root/.dotnet/tools"
# Version is pinned to the EF Core packages used by the solution.
RUN dotnet tool install --global dotnet-ef --version 10.0.11
# `dotnet ef` boots the API host to discover the DbContext and the API refuses
# to start without a valid Jwt:SigningKey. This inline value only exists for
# this single build command: it is not an ENV, is not used at runtime and
# never reaches an image layer's environment. The real connection string is
# supplied when the bundle runs (--connection).
RUN case "${TARGETARCH:-amd64}" in \
      amd64) RID=linux-x64 ;; \
      arm64) RID=linux-arm64 ;; \
      *) echo "Unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac \
    && Jwt__SigningKey="build-time-only-placeholder-not-a-secret-0000" \
    dotnet ef migrations bundle \
      --project src/Conversia.Infrastructure \
      --startup-project src/Conversia.API \
      --configuration Release \
      --self-contained -r "$RID" \
      --output /out/efbundle

# -------------------------------------------------------------- migrator
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS migrator
WORKDIR /app
COPY --from=bundle /out/efbundle ./efbundle
ARG APP_VERSION=0.0.0-local
ARG GIT_SHA=unknown
ARG BUILD_DATE=unknown
ARG SOURCE_URL=https://github.com/Daelres/Conversia-Analytics
LABEL org.opencontainers.image.title="conversia-migrator" \
      org.opencontainers.image.description="Conversia EF Core migrations bundle (one-shot job)" \
      org.opencontainers.image.version="${APP_VERSION}" \
      org.opencontainers.image.revision="${GIT_SHA}" \
      org.opencontainers.image.created="${BUILD_DATE}" \
      org.opencontainers.image.source="${SOURCE_URL}"
# Non-root user (the "app" user, uid 1654, ships with the .NET images).
USER $APP_UID
# Pass --connection "<connection string>" (compose sets it from the environment).
ENTRYPOINT ["./efbundle"]

# ----------------------------------------------------------------- final
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
# Liveness probe (bash /dev/tcp, no curl in the image): see docker/healthcheck.sh and docs/DOCKER.md.
COPY --chmod=0555 docker/healthcheck.sh /usr/local/bin/healthcheck
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 \
  CMD ["/usr/local/bin/healthcheck", "/health"]
ARG APP_VERSION=0.0.0-local
ARG GIT_SHA=unknown
ARG BUILD_DATE=unknown
ARG SOURCE_URL=https://github.com/Daelres/Conversia-Analytics
LABEL org.opencontainers.image.title="conversia-api" \
      org.opencontainers.image.description="Conversia API: WhatsApp webhook ingestion, classification and metrics" \
      org.opencontainers.image.version="${APP_VERSION}" \
      org.opencontainers.image.revision="${GIT_SHA}" \
      org.opencontainers.image.created="${BUILD_DATE}" \
      org.opencontainers.image.source="${SOURCE_URL}"
# Non-root user (the "app" user, uid 1654, ships with the .NET images).
USER $APP_UID
# No secrets are baked into the image: ConnectionStrings__DefaultConnection and
# Jwt__SigningKey must be supplied at run time (environment variables).
ENTRYPOINT ["dotnet", "Conversia.API.dll"]
