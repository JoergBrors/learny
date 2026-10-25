# syntax=docker/dockerfile:1
# ─── LearnCards Web (Blazor + API) ──────────────────────────────────────────
# Mehrstufiger Build: SDK-Image kompiliert, schlankes ASP.NET-Runtime-Image läuft.
# Das Projekt hat bewusst keine externen NuGet-Abhängigkeiten (ADR 0002) -> der Build
# funktioniert auch ohne Zugriff auf nuget.org (siehe /NuGet.config).
# Mehrere Plattformen (linux/amd64, linux/arm64): Veröffentlicht wird plattformunabhängig
# (framework-abhängig, ohne native Anteile) auf der Build-Plattform; nur die Laufzeit-Stufe ist zielplattformspezifisch.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NuGet.config .
COPY src/LearnCards.Web/LearnCards.Web.csproj src/LearnCards.Web/
RUN dotnet restore src/LearnCards.Web/LearnCards.Web.csproj

COPY src/LearnCards.Web/ src/LearnCards.Web/
RUN dotnet publish src/LearnCards.Web/LearnCards.Web.csproj \
    -c Release -o /app/publish --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG VERSION=dev
ARG REVISION=unknown
ARG CREATED=unknown
LABEL org.opencontainers.image.title="learny-web" \
      org.opencontainers.image.description="Learny (LearnCards): Karteikarten-Lernplattform mit Blazor-Oberfläche und REST-API" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.created="${CREATED}" \
      org.opencontainers.image.source="https://github.com/JoergBrors/learny" \
      org.opencontainers.image.url="https://github.com/JoergBrors/learny" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app

# libsqlite3 wird per P/Invoke für den SQLite-Fallback-Provider benötigt (auf amd64 und arm64 verfügbar);
# wget nur für den Container-Healthcheck (das Basis-Image enthält weder wget noch curl).
RUN apt-get update \
    && apt-get install -y --no-install-recommends libsqlite3-0 ca-certificates wget \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Das Basis-Image bringt den unprivilegierten Benutzer "app" (UID 1654) mit; /app/data ist das einzige beschreibbare Verzeichnis.
RUN mkdir -p /app/data && chown -R 1654:1654 /app/data
USER 1654

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
# Datenbank (SQLite), Data-Protection-Schlüssel, Badge-Signaturschlüssel und lokaler Bildspeicher
VOLUME ["/app/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD wget -qO- http://127.0.0.1:8080/health >/dev/null || exit 1

ENTRYPOINT ["dotnet", "LearnCards.Web.dll"]
