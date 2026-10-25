# syntax=docker/dockerfile:1
# ─── LearnCards MCP Server ───────────────────────────────────────────────────
# Eigenständiger MCP-Server (ADR 0007): HTTP-Transport (/mcp) für den Dauerbetrieb im Compose-Profil "mcp",
# stdio für Clients, die den Container direkt starten (z. B. `docker run -i --rm … joergb23/learny-mcp`).
# Ohne weitere Einstellung startet der Server im stdio-Modus (Standard der Anwendung).

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NuGet.config .
COPY src/LearnCards.McpServer/LearnCards.McpServer.csproj src/LearnCards.McpServer/
RUN dotnet restore src/LearnCards.McpServer/LearnCards.McpServer.csproj

COPY src/LearnCards.McpServer/ src/LearnCards.McpServer/
RUN dotnet publish src/LearnCards.McpServer/LearnCards.McpServer.csproj \
    -c Release -o /app/publish --no-restore -p:UseAppHost=false

# Der HTTP-Transport nutzt ASP.NET Core (WebApplication) -> ASP.NET-Runtime statt reiner .NET-Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG VERSION=dev
ARG REVISION=unknown
ARG CREATED=unknown
LABEL org.opencontainers.image.title="learny-mcp" \
      org.opencontainers.image.description="Learny MCP-Server: Model-Context-Protocol-Zugang zu Learny (HTTP /mcp oder stdio)" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.created="${CREATED}" \
      org.opencontainers.image.source="https://github.com/JoergBrors/learny" \
      org.opencontainers.image.url="https://github.com/JoergBrors/learny" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app

# wget nur für den Container-Healthcheck (HTTP-Betrieb)
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates wget \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

USER 1654

ENV DOTNET_EnableDiagnostics=0
# Konfiguration ausschließlich über Umgebungsvariablen (LEARNCARDS_*) bzw. eine eingehängte mcpsettings.json (LEARNCARDS_MCP_CONFIG); keine Geheimnisse im Image.

ENTRYPOINT ["dotnet", "LearnCards.McpServer.dll"]
