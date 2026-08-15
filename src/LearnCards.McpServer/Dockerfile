# ─── LearnCards MCP Server ───────────────────────────────────────────────────
# Eigenständiger stdio-MCP-Server. Läuft im Compose-Profil "mcp" interaktiv
# (stdin_open/tty), z. B. über `docker compose run --rm mcp-server` oder
# angebunden an einen MCP-Client, der den Container per stdio startet.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NuGet.config .
COPY src/LearnCards.McpServer/LearnCards.McpServer.csproj src/LearnCards.McpServer/
RUN dotnet restore src/LearnCards.McpServer/LearnCards.McpServer.csproj

COPY src/LearnCards.McpServer/ src/LearnCards.McpServer/
RUN dotnet publish src/LearnCards.McpServer/LearnCards.McpServer.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

RUN useradd --uid 1654 --create-home --shell /usr/sbin/nologin learncards
USER learncards

ENTRYPOINT ["dotnet", "LearnCards.McpServer.dll"]
