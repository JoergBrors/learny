# ─── LearnCards Web (Blazor + API) ──────────────────────────────────────────
# Mehrstufiger Build: SDK-Image kompiliert, schlankes ASP.NET-Runtime-Image läuft.
# Das Projekt hat bewusst keine externen NuGet-Abhängigkeiten -> der Build
# funktioniert auch ohne Zugriff auf nuget.org (siehe /NuGet.config).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NuGet.config .
COPY src/LearnCards.Web/LearnCards.Web.csproj src/LearnCards.Web/
RUN dotnet restore src/LearnCards.Web/LearnCards.Web.csproj

COPY src/LearnCards.Web/ src/LearnCards.Web/
RUN dotnet publish src/LearnCards.Web/LearnCards.Web.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# libsqlite3 wird per P/Invoke für den SQLite-Fallback-Provider benötigt
# (im Docker-Betrieb kommt üblicherweise PostgreSQL zum Einsatz — siehe DB_PROVIDER)
RUN apt-get update \
    && apt-get install -y --no-install-recommends libsqlite3-0 ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

RUN useradd --uid 1654 --create-home --shell /usr/sbin/nologin learncards \
    && mkdir -p /app/data \
    && chown -R learncards:learncards /app
USER learncards

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
VOLUME ["/app/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD wget -qO- http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "LearnCards.Web.dll"]
