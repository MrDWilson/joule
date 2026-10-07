# syntax=docker/dockerfile:1
# Joule: multi-arch (linux/amd64, linux/arm64) image. The web and SDK stages run on the build machine's own
# architecture and cross-target the runtime, so an arm64 image builds quickly on an x64 runner (and vice versa).

FROM --platform=$BUILDPLATFORM node:24-bookworm-slim AS web
WORKDIR /build/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS api
ARG TARGETARCH
ARG VERSION=1.1.1
ARG REVISION=""
WORKDIR /build
COPY src/Joule.Api/Joule.Api.csproj src/Joule.Api/
# TARGETARCH is set by BuildKit (amd64/arm64); the legacy builder leaves it empty and gets the build machine's RID.
RUN dotnet restore src/Joule.Api/Joule.Api.csproj ${TARGETARCH:+-a $TARGETARCH}
COPY src/Joule.Api/ src/Joule.Api/
RUN dotnet publish src/Joule.Api/Joule.Api.csproj --no-restore -c Release ${TARGETARCH:+-a $TARGETARCH} --self-contained false \
      -p:Version="$VERSION" ${REVISION:+-p:SourceRevisionId=$REVISION} -o /publish
COPY --from=web /build/web/dist/ /publish/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
ARG VERSION=1.1.1
ARG REVISION=""
LABEL org.opencontainers.image.title="Joule" \
      org.opencontainers.image.description="A self-hosted dashboard and AI analyst that works alongside the Predbat home battery optimiser." \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.source="https://github.com/MrDWilson/joule" \
      org.opencontainers.image.url="https://github.com/MrDWilson/joule" \
      org.opencontainers.image.documentation="https://github.com/MrDWilson/joule#readme" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION"
# No App__Demo here: Joule starts in demo mode by default, and Setup's "Connect my Predbat" can then switch it to live
# (an App__Demo environment variable would pin the mode and override that choice).
# HTTP_PORTS is cleared so ASP.NET doesn't warn that ASPNETCORE_URLS overrides the base image's port 8080.
ENV ASPNETCORE_URLS=http://0.0.0.0:5080 \
    HTTP_PORTS= \
    App__DataDirectory=/data \
    TZ=Europe/London
WORKDIR /app
COPY --from=api /publish/ ./
RUN mkdir -p /data && chown app:app /data && chmod 700 /data
USER app
EXPOSE 5080
VOLUME /data
# The aspnet image has no curl or wget, so the app probes its own unauthenticated /api/health.
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 CMD ["dotnet", "Joule.Api.dll", "--health"]
ENTRYPOINT ["dotnet", "Joule.Api.dll"]
