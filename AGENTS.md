# AGENTS.md — Leitfaden für KI-Assistenten und Entwickler

LearnCards: Flashcard-Lernplattform in C#/.NET 10. Blazor Web App (Server-Interaktivität) + REST-API + OIDC in
`src/LearnCards.Web`, eigenständiger MCP-Server in `src/LearnCards.McpServer`. Sprache von Code-Kommentaren,
UI-Texten und Dokumentation: **Deutsch**; Bezeichner: Englisch.

## Vorgehen bei jeder Änderung

1. Betroffenen Bereich in der Tabelle unten finden und die verlinkte **ADR zuerst lesen** (Abschnitt „Leitplanken für Änderungen“).
2. Kleinste passende Änderung im bestehenden Stil umsetzen; Nachbarcode als Vorbild nehmen.
3. Bauen: `dotnet build LearnCards.slnx` — muss fehlerfrei sein. Danach Integrationstests: `dotnet run --project tests/LearnCards.IntegrationTests --no-build`
   (siehe [docs/testing.md](docs/testing.md)); bei Änderungen an Benutzerbezug, Daten oder API dort Prüfungen ergänzen. UI-Verhalten per `dotnet run`
   ([docs/local-development.md](docs/local-development.md)) prüfen und im Ergebnis benennen, was geprüft wurde.
4. Dokumentation mitziehen (README/`docs/`/Help-Seite/`.env.example`), falls Verhalten oder Konfiguration sich ändert.
5. Widerspricht die Änderung einer ADR: nicht stillschweigend umgehen — neue ADR schreiben (Status der alten: „Ersetzt durch“)
   oder beim Auftraggeber nachfragen.

## Wo ändere ich was?

| Aufgabe | Dateien | ADR |
|---|---|---|
| Kartenbilder (`images[]`), Limits, Darstellung | `Domain/CardImages.cs` (Limits + Validator), `FlashCardView.razor`, `CardImportSchema.cs`, MCP-Tool-Texte | [0016](docs/adr/0016-bilder-in-lernkarten.md), [0006](docs/adr/0006-kanonisches-karten-json.md) |
| Bildspeicher (Provider, Cache, Migration), Image-Proxy `/api/images/{id}`, SAS-/Presign-Broker, SSRF-Schutz | `Services/ImageStorage/*` (Provider, `SafeHttp`, `StorageUrlGuard`, `CardImageService`, `ImageMigrationService`), `Infrastructure/ImageStorageSettings.cs`, `ApiEndpoints.cs` (`/images`, `/admin/images`) | [0017](docs/adr/0017-konfigurierbare-bildspeicher.md), [0018](docs/adr/0018-zentraler-image-proxy-und-storage-zugriff.md) |
| Kartenfeld hinzufügen/ändern | `Domain/Models.cs`, `Data/Schema.cs`, `Services/CardRepository.cs`, `Api/CardImportSchema.cs`, MCP `Program.cs`, `wwwroot/templates/*`, `Help.razor`, `FlashCardView.razor` | [0006](docs/adr/0006-kanonisches-karten-json.md), [0004](docs/adr/0004-datenbank-abstraktion.md) |
| Neuer REST-Endpunkt | `Api/ApiEndpoints.cs` (+ Service-Methode) | [0003](docs/adr/0003-eine-deploybare-einheit.md), [0005](docs/adr/0005-authentifizierung.md) |
| Neues MCP-Tool | `LearnCards.McpServer/Program.cs` (+ API-Endpunkt, in `TokenAllowedPrefixes`) | [0007](docs/adr/0007-mcp-server.md), [0013](docs/adr/0013-mcp-benutzergebundene-anmeldung.md) |
| MCP-Anmeldung, Tokens, Zustimmung | `Services/McpLinkService.cs`, `McpConnect.razor`, MCP `ProprietaryOAuthService.cs` | [0013](docs/adr/0013-mcp-benutzergebundene-anmeldung.md) |
| Datenbank/SQL/Schema | `Data/*`, `CardRepository.cs`, [docs/database-schema.md](docs/database-schema.md) mitpflegen | [0004](docs/adr/0004-datenbank-abstraktion.md) |
| Login, OIDC, API-Schutz | `Auth/*`, API-Filter in `ApiEndpoints.cs` | [0005](docs/adr/0005-authentifizierung.md), [0011](docs/adr/0011-mehrbenutzerarchitektur.md) |
| Benutzer, Rollen, Besitz, Profil/Admin-UI | `Services/UserService.cs`, `Domain/Users.cs`, `Pages/Profile.razor`, `Pages/Admin.razor` | [0011](docs/adr/0011-mehrbenutzerarchitektur.md), [0012](docs/adr/0012-rollen-aus-entra-app-roles.md) |
| Freigaben, Einreichung, globale Module, globale Ergebnisse | `Services/SharingService.cs`, `CardRepository.Sharing.cs`, `SharePanel.razor`, `Admin.razor` | [0015](docs/adr/0015-freigaben-globale-lernmodule.md) |
| Chat, Quiz-Generierung, Bewertung | `Services/OpenAiClient.cs`, `QuizService.cs`, `ChatService.cs` | [0008](docs/adr/0008-ki-integration-und-fallback.md) |
| Folienmodus, Karten-Quiz | `CardQuizPanel.razor`, `FlashCardView.razor` | [0010](docs/adr/0010-folienmodus-und-karten-quiz.md) |
| Neue Umgebungsvariable | `Infrastructure/AppConfig.cs`, `.env.example`, `deploy/iis/web.env`, `docker-compose.yml`, Deployment-Doku | [0009](docs/adr/0009-deployment-wege.md) |
| UI/Styling | `Components/**`, `wwwroot/app.css` (Farbsystem, Dark Mode beachten) | [0003](docs/adr/0003-eine-deploybare-einheit.md) |
| Neue Abhängigkeit | — verboten ohne neue ADR | [0002](docs/adr/0002-keine-nuget-pakete.md) |

## Harte Regeln

- **Keine NuGet-Pakete** hinzufügen ([0002](docs/adr/0002-keine-nuget-pakete.md)).
- **SQL nur mit gebundenen Parametern**; Abfragen müssen auf SQLite *und* PostgreSQL laufen ([0004](docs/adr/0004-datenbank-abstraktion.md)).
- **Bildspeicher** ([0017](docs/adr/0017-konfigurierbare-bildspeicher.md), [0018](docs/adr/0018-zentraler-image-proxy-und-storage-zugriff.md)): Bilder erreichen Clients **nur** über `GET /api/images/{id}` (Berechtigung → Cache → Speicher, keine Redirects); Metadaten in `card_images`, Bytes über `IImageStorageProvider`; Speicherschlüssel nur serverseitig (32 Hex) und in jedem Provider validiert; Remote-Lesen nur über `IStorageAccessBroker` (SAS `sp=r`, `sr=b`, TTL 60 s) und `StorageUrlGuard`; SAS/Signaturen/Schlüssel nie in DB, Logs, Fehlermeldungen oder Antworten (`SecretRedactor`); ausgehende Aufrufe nur über `SafeHttp` (kein Redirect, IP-Prüfung, Größenlimit); Migration nur ausdrücklich, Quelle wird nie gelöscht; keine Secrets im Code.
- **Kartenbilder** ([0016](docs/adr/0016-bilder-in-lernkarten.md)) (Format/Limits; Speicherung siehe oben): Limits nur in `CardImageLimits`; jeder Schreibpfad nutzt `CardImageValidator`; Listen liefern nur Metadaten; fehlendes `images` behält bestehende Bilder; keine externen URLs, kein SVG, Base64 nie in Prompts oder Logs.
- **Karten-JSON ist ein Vertrag**: Felder nicht umbenennen/entfernen, neue Felder optional ([0006](docs/adr/0006-kanonisches-karten-json.md)).
- **KI-Funktionen behalten ihren Offline-Fallback** ([0008](docs/adr/0008-ki-integration-und-fallback.md)).
- **Module/Karten sind privat** (Eigentümer). Zusätzlich gibt es **explizit verwaltete** freigegebene Module (Eigentümer erlaubt Lernen) und **globale** Module (Administrator veröffentlicht eine eingefrorene Version) — [ADR 0015](docs/adr/0015-freigaben-globale-lernmodule.md). Lesen fremder Module nur über `ResolveModuleAccessAsync`/`*Readable*`, Ändern nur mit `ownerId`; kein „Admin liest alles“.
- **Mehrbenutzer-Isolation** ([0011](docs/adr/0011-mehrbenutzerarchitektur.md)): Jeder Zugriff auf Module/Karten läuft mit `ownerId` und filtert in SQL; die Benutzer-ID stammt nur aus dem Claim `uid`/`HttpContext.Items`, nie aus Request-Daten. Rollen für Änderungen über `UserService.RequireRoleAsync` (DB) prüfen.
- **MCP**: Daten nur mit dem Token des Benutzers ([0013](docs/adr/0013-mcp-benutzergebundene-anmeldung.md)); der Dienstschlüssel `MCP_API_KEY` gewährt nie Datenzugriff und wird nie an Clients weitergegeben. Tokens/Codes nie im Klartext speichern oder loggen.
- Geschäftslogik nicht in `.razor`-Dateien oder Endpunkt-Lambdas, sondern in `Services/`.
- **Keine Secrets** committen (`.env`, `mcpsettings.json`, Schlüssel). `src/LearnCards.Web/Data/keys/` enthält einen
  eingecheckten Data-Protection-Schlüssel — nicht erweitern oder wiederverwenden; Schlüssel gehören nicht ins Repository.
- **Betriebsmodus:** Dev-Login (`AUTH_MODE=dev`) ist nie für Produktion; `auto` fällt ohne OIDC lautlos auf Dev zurück (Prüfbericht F1, Härtung vorgeschlagen in ADR 0014).
- **Nicht ohne ausdrücklichen Auftrag:** Git-Tags setzen/pushen (löst Produktions-Deployment aus), Daten löschen, Deployments auslösen.
- `bin/`, `obj/`, `publish-web-test/` sind Build-Ausgaben, keine Quellen.

## Weiterführend

- Systemüberblick: [docs/architecture.md](docs/architecture.md) · Datenmodell: [docs/database-schema.md](docs/database-schema.md) · Entwicklungsablauf mit KI: [docs/ai-development.md](docs/ai-development.md)
- Tests: [docs/testing.md](docs/testing.md) · Prüfbericht Mehrbenutzer (offene Punkte F1–F11, Vorschlag ADR 0014): [docs/reviews/](docs/reviews/2026-10-24-mehrbenutzer-pruefbericht.md)
- Alle Entscheidungen: [docs/adr/README.md](docs/adr/README.md)
