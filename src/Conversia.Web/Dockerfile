# syntax=docker/dockerfile:1
# Build context is the repository root:
#   docker build -f src/Conversia.Web/Dockerfile -t conversia-web .
# Optional build args (scripts/docker-build.sh sets them from git): APP_VERSION, GIT_SHA, BUILD_DATE, SOURCE_URL.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only the project files first so the restore layer is cached until a
# package reference or project reference changes. The Web depends on
# Application (and Domain transitively) only.
COPY src/Conversia.Domain/Conversia.Domain.csproj src/Conversia.Domain/
COPY src/Conversia.Application/Conversia.Application.csproj src/Conversia.Application/
COPY src/Conversia.Web/Conversia.Web.csproj src/Conversia.Web/
RUN dotnet restore src/Conversia.Web/Conversia.Web.csproj

COPY src/Conversia.Domain/ src/Conversia.Domain/
COPY src/Conversia.Application/ src/Conversia.Application/
COPY src/Conversia.Web/ src/Conversia.Web/
# No --no-restore on purpose. The restore above only sees the .csproj files, and the SDK adds the ASP.NET Core
# internal assets package (it carries _framework/blazor.web.js) only when the project has Razor components. Publishing
# without a second restore leaves that script out of the image, so every page asks for it and gets a 404.
RUN dotnet publish src/Conversia.Web/Conversia.Web.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# Liveness probe (bash /dev/tcp, no curl in the image): see docker/healthcheck.sh and docs/DOCKER.md.
COPY --chmod=0555 docker/healthcheck.sh /usr/local/bin/healthcheck
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=3 \
  CMD ["/usr/local/bin/healthcheck", "/health"]
ARG APP_VERSION=0.0.0-local
ARG GIT_SHA=unknown
ARG BUILD_DATE=unknown
ARG SOURCE_URL=https://github.com/Daelres/Conversia-Analytics
LABEL org.opencontainers.image.title="conversia-web" \
      org.opencontainers.image.description="Conversia Web: executive dashboard (Blazor Web)" \
      org.opencontainers.image.version="${APP_VERSION}" \
      org.opencontainers.image.revision="${GIT_SHA}" \
      org.opencontainers.image.created="${BUILD_DATE}" \
      org.opencontainers.image.source="${SOURCE_URL}"
# Non-root user (the "app" user, uid 1654, ships with the .NET images).
USER $APP_UID
# No secrets are baked into the image. Point the Web at the API at run time
# with the Api__BaseUrl environment variable.
ENTRYPOINT ["dotnet", "Conversia.Web.dll"]
