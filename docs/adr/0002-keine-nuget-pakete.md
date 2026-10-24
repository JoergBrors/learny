# 0002 — Keine externen NuGet-Pakete

- **Status:** Akzeptiert
- **Datum:** 2026-10-24 (dokumentiert; Entscheidung seit der .NET-Portierung)

## Kontext

Build- und Deploy-Umgebungen (Firmenproxys, IIS-Runner) haben teils keinen NuGet-Zugriff. Außerdem
soll das Wire-Verhalten (PostgreSQL-SCRAM, OIDC-Validierung) nachvollziehbar bleiben.

## Entscheidung

Beide Projekte (`LearnCards.Web`, `LearnCards.McpServer`) nutzen ausschließlich .NET-SDK und
ASP.NET-Core-Framework. Datenbankzugriff, OIDC und OpenAI-Client sind selbst implementiert
(`HttpClient`, Sockets, P/Invoke auf `libsqlite3`).

## Konsequenzen

- `dotnet build`/`publish` funktionieren offline mit nur dem SDK.
- Mehr eigener Code (z. B. `PostgresDatabase.cs`, ~500 Zeilen) und eigene Verantwortung für Sicherheit.

## Leitplanken für Änderungen

- **Kein `PackageReference` hinzufügen**, auch nicht für Tests oder Hilfsbibliotheken, ohne neue ADR. Die Tests (`tests/LearnCards.IntegrationTests`) sind deshalb eine paketfreie Konsolenanwendung (siehe [testing.md](../testing.md)).
- Der Austausch gegen Standardpakete ist über `IDatabase` und `OpenAiClient` vorbereitet (siehe
  [0004](0004-datenbank-abstraktion.md)); er darf API/UI nicht verändern.
- `NuGet.config` nicht um externe Quellen erweitern.

## Betroffener Code

- `src/*/*.csproj`, `NuGet.config`, `src/LearnCards.Web/Data/*`, `Auth/OidcService.cs`, `Services/OpenAiClient.cs`
