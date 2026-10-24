# 0011 — Mehrbenutzerarchitektur: Identität, Besitz, Rollen, Migration

- **Status:** Akzeptiert
- **Datum:** 2026-10-24
- **Teilweise ersetzt durch:** [0013](0013-mcp-benutzergebundene-anmeldung.md) (`MCP_LINKED_USER_ID` entfällt; MCP-Zugriff läuft über benutzergebundene Tokens)
- **Geprüft in:** [Mehrbenutzer-Prüfbericht](../reviews/2026-10-24-mehrbenutzer-pruefbericht.md); Härtungsvorschlag: [0014](0014-sitzungs-und-betriebsmodus-haertung.md)
- **Ergänzt durch:** [0012](0012-rollen-aus-entra-app-roles.md) (Rollen aus Entra-ID-App-Rollen)
- **Ergänzt:** [0005](0005-authentifizierung.md) (Authentifizierung), [0004](0004-datenbank-abstraktion.md) (Schemaänderungen)

## Kontext

Bis zu dieser Entscheidung waren Module und Karten global: jeder angemeldete Benutzer sah und änderte alles, `modules.name`
war global eindeutig, und Aufrufe mit `X-MCP-Key` liefen unter dem Pseudo-Benutzer `"mcp-client"`. Nur Quiz-Ergebnisse,
Karten-Status, Chat-Verlauf und Theme waren über `user_sub` (das externe IdP-Subject) getrennt. Learny soll mehrbenutzerfähig
werden; Freigaben, globale Inhalte und benutzergebundenes MCP folgen darauf aufbauend.

## Entscheidung

### Identitätsmodell
- Jeder Benutzer hat eine **stabile interne ID** (`users.id`, UUID), unabhängig vom Identity Provider.
- Externe Identitäten (`user_identities`: `provider` + `subject` → `user_id`, eindeutig) werden beim ersten Login angelegt.
  Provider ist die OIDC-Issuer-URL bzw. `dev`. Ein Benutzer kann mehrere Identitäten haben (vorbereitet, noch keine UI).
- Beim Login setzt der Server den Cookie-Claim **`uid`**. Er ist die einzige Grundlage für Autorisierung; Client-Angaben
  (Header, Body, Query) zur Benutzer-ID werden nie ausgewertet.
- Bei **jeder** Cookie-Anfrage (`OnValidatePrincipal`) wird der Benutzer gegen die DB geprüft (existiert, `status = active`);
  Rollen-Claims werden frisch aus der DB gesetzt, nicht aus dem Cookie gelesen. Cookies ohne `uid` (vor dieser ADR) werden abgelehnt.
- Gesperrte Benutzer (`status = disabled`) werden sofort abgewiesen (API 401, kein neuer Login).

### Besitz und Datenisolation
- `modules.owner_user_id` und `cards.owner_user_id` (FK → `users`, `ON DELETE SET NULL`). Der Kartenbesitzer ist immer der Modulbesitzer.
- `modules.name` ist nur noch **pro Eigentümer** eindeutig (`ux_modules_owner_name`).
- Nutzerbezogene Tabellen (`user_preferences`, `user_card_states`, `chat_history`, `quiz_results`) halten in `user_sub` jetzt die
  **interne Benutzer-ID** (Spaltenname aus Kompatibilitätsgründen unverändert, kein Fremdschlüssel).
- **Jede** Repository-Methode, die Module/Karten liest oder ändert, bekommt `ownerId` und filtert in SQL (`WHERE owner_user_id = @owner`).
  Fremde oder unzugeordnete Datensätze verhalten sich wie „nicht vorhanden“ (404, leere Liste).
- Quiz-Ergebnisse dürfen nur auf eigene Module/Karten verweisen. Karten-Status und Chat nur für eigene Karten.
- Vom Client vorgegebene Karten-IDs werden nur übernommen, wenn sie global noch nicht existieren; bei Kollision entsteht eine neue ID
  (kein Überschreiben/Erraten fremder Karten, Import fremder Dateien bleibt möglich).
- Ungültige IDs (kein GUID) werden früh abgewiesen (404 statt Datenbankfehler).

### Rollen und Berechtigungen
Ein Benutzer hat mehrere Rollen (`user_roles`); `user` besitzt jeder Benutzer immer.

| Fähigkeit | `user` | `admin` | `auditor` |
|---|:-:|:-:|:-:|
| Eigene Module/Karten/Ergebnisse verwalten, Anzeigename ändern | ✔ | ✔ | ✔ |
| Fremde **private** Module/Karten/Ergebnisse lesen oder ändern | ✘ | ✘ | ✘ |
| Benutzer sperren/entsperren, Rollen vergeben | ✘ | ✔ | ✘ |
| Unzugeordneten Altbestand einem Benutzer zuweisen | ✘ | ✔ | ✘ |
| Audit-Log lesen | ✘ | ✔ | ✘ |
| Globale Inhalte/Ergebnisse lesen (folgt mit Auftrag 2) | — | — | ✔ (Lesen) |
| Veröffentlichen/Bearbeiten globaler Inhalte (folgt mit Auftrag 2) | — | ✔ | ✘ |

- Auch Administratoren sehen **keine fremden privaten Inhalte** über die normale API. Administration ≠ Dateneinsicht.
- Rollen werden ausschließlich über `UserService` vergeben, der die Rolle des Aufrufers **gegen die DB** prüft
  (`RequireRoleAsync`), nicht gegen Claims. Der letzte aktive Admin kann weder Rolle noch Zugang verlieren; man kann sich nicht selbst sperren.
- Die Rolle `auditor` ist angelegt und zuweisbar, gewährt in dieser Ausbaustufe aber noch keine zusätzlichen Zugriffe
  (es gibt noch keine globalen Inhalte). Weitere Rollen/Rechte werden bei Bedarf in der ADR zum Freigabemodell entschieden.
- Bootstrap: Subjects in `ADMIN_SUBJECTS` (kommagetrennt, IdP-`sub`; **nicht** E-Mail, da nicht verlässlich verifiziert) erhalten beim Login
  die Rolle `admin`; im Dev-Modus zusätzlich der Standardbenutzer `local-dev-user`.
- Administrative Aktionen (Rollen, Sperren, Zuweisung, Erstanlage) werden in `audit_log` protokolliert.

### API und MCP-Schlüssel (überholt durch [0013](0013-mcp-benutzergebundene-anmeldung.md))
- `/api` verlangt Cookie-Login **oder** `X-MCP-Key` (Vergleich zeitkonstant).
- Der gemeinsame Schlüssel ist nur ein **technischer** Zugang. Er handelt ausschließlich im Kontext des Benutzers `MCP_LINKED_USER_ID`
  (Übergangslösung). Ist die Variable leer, erhalten Schlüssel-Clients 403 auf allen benutzerbezogenen Endpunkten
  (nur `/api/schema/import/cards` bleibt erreichbar). Administrative Endpunkte (`/api/admin/*`) sind über den Schlüssel nie erreichbar.
- Benutzergebundene MCP-Zugänge (Verknüpfung im Profil, Tokens, Widerruf) sind Gegenstand einer eigenen ADR (Auftrag 3).

### Migration bestehender Daten (wiederholbar, ohne Datenverlust)
1. Neue Tabellen `users`, `user_identities`, `user_roles`, `audit_log` per `CREATE TABLE IF NOT EXISTS`.
2. Neue Spalten `owner_user_id` per idempotenter Spaltenmigration (PostgreSQL: `PostgresDatabase.MigrateOwnershipAsync`;
   SQLite: `SqliteDatabase.MigrateOwnership`). Bestehende Zeilen behalten `owner_user_id = NULL` („unzugeordnet“).
3. PostgreSQL: Constraint `modules_name_key` wird entfernt. SQLite: `modules` wird im offiziellen Rebuild-Verfahren neu aufgebaut
   (FK aus, Tabelle kopieren, tauschen, FK ein), weil das Inline-`UNIQUE` nicht per `ALTER` entfernbar ist.
4. Besitz-Indizes (`Schema.OwnershipIndexStatements`) laufen **nach** den Spaltenmigrationen.
5. Beim ersten Login einer Identität werden die Altdaten dieses Subjects (`user_sub = <externes Subject>`) in den vier nutzerbezogenen Tabellen
   auf die interne ID umgeschrieben; danach `users.legacy_migrated = 1`. Es werden nur Daten genau dieses Subjects berührt.
6. **Ungeklärte Eigentümer werden nie automatisch zugeordnet.** Unzugeordnete Module sind für normale Benutzer unsichtbar; ein Administrator
   weist sie unter `/admin` (bzw. `POST /api/admin/modules/{id}/owner`) explizit einem Benutzer zu (Namenskonflikt → Fehler, Audit-Eintrag).
7. Rückfall: Vor dem Rollout Datenbank sichern. Die Migration löscht keine Daten; die alten Spalten/Tabellen bleiben bestehen. Ein Rollback auf die
   Vorversion ist nur ohne zwischenzeitlich neu angelegte Benutzer-/Besitzdaten sinnvoll (die Vorversion kennt `owner_user_id` nicht, würde aber weiterlaufen;
   `user_sub`-Werte wären dann interne IDs, Karten-/Modulzugriff global). Siehe [database-schema.md](../database-schema.md).

## Konsequenzen

- **Breaking Change für den MCP-Server:** Schlüssel-Clients erhalten 403 (die ursprüngliche Übergangslösung `MCP_LINKED_USER_ID` wurde mit [0013](0013-mcp-benutzergebundene-anmeldung.md) durch benutzergebundene Tokens ersetzt).
- Nach dem Rollout muss mindestens ein Administrator konfiguriert werden (`ADMIN_SUBJECTS`), sonst bleibt der Altbestand unzugeordnet.
- Pro Cookie-Anfrage entsteht eine kleine DB-Abfrage (Benutzerprüfung); dafür wirken Sperren und Rollenänderungen sofort.
- Blazor-Circuits tragen den Principal vom Verbindungsaufbau; Admin-Operationen prüfen die Rolle zusätzlich gegen die DB.
- Wechselt der OIDC-Issuer, entstehen neue Identitäten (neue Benutzer); eine Identitätszusammenführung ist noch nicht umgesetzt.

## Leitplanken für Änderungen

- **Jede neue Repository-Methode auf Modulen/Karten braucht einen `ownerId`-Parameter und filtert damit in SQL.** Nie nur nach `id` lesen oder schreiben.
- Die Benutzer-ID kommt **ausschließlich** aus dem Claim `uid` (Blazor) bzw. aus `HttpContext.Items["lc.user"]` (API, gesetzt vom Endpoint-Filter).
  Niemals aus Request-Parametern oder Headern.
- Neue API-Endpunkte gehören in die Gruppe `secured` (setzt einen Benutzerkontext voraus); administrative in `/admin` mit Rollenprüfung gegen die DB.
- Rollenprüfungen für Änderungen immer über `UserService.RequireRoleAsync` (DB), nicht über `User.IsInRole` allein.
- Neue Tabellen mit Benutzerbezug: Besitz-/Benutzerspalte, Isolationstest in `tests/LearnCards.IntegrationTests` ergänzen, [database-schema.md](../database-schema.md) aktualisieren.
- Neue Datenabfragen mit Benutzereingaben: IDs mit `UserService.IsGuid` prüfen (PostgreSQL wirft sonst Typfehler).
- Sicherheitsrelevante Rollen dürfen nie über das Profil (`/api/me`) vergeben werden.

## Betroffener Code

- `Domain/Users.cs`, `Services/UserService.cs`, `Services/CardRepository.cs`, `Services/QuizService.cs`, `Services/ChatService.cs`
- `Data/Schema.cs`, `Data/PostgresDatabase.cs`, `Data/SqliteDatabase.cs`
- `Api/ApiEndpoints.cs`, `Auth/AuthEndpoints.cs`, `Program.cs`, `Infrastructure/AppConfig.cs`
- `Components/Pages/Profile.razor`, `Admin.razor`, `Components/Layout/MainLayout.razor`
- `tests/LearnCards.IntegrationTests`
