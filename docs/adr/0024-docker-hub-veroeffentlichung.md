# 0024 — Veröffentlichung als Docker-Hub-Images mit Compose-Installationspaket

- **Status:** Akzeptiert (Erstveröffentlichung steht aus)
- **Datum:** 2026-10-26
- **Ergänzt:** [0009](0009-deployment-wege.md) (zusätzlicher Verteilweg, bestehende Wege bleiben unverändert)
- **Bezug:** [0002](0002-keine-nuget-pakete.md), [0003](0003-eine-deploybare-einheit.md), [0007](0007-mcp-server.md), [0013](0013-mcp-benutzergebundene-anmeldung.md), [0017](0017-konfigurierbare-bildspeicher.md), [0021](0021-zertifikate-und-open-badges.md), [0023](0023-secrets-und-badge-schluessel-unter-iis.md); Prüfbericht F1 / Vorschlag ADR 0014 (Betriebsmodus)

## Kontext

Anwender sollen Learny ohne Quellcode, Git und .NET SDK betreiben können. Bisher gab es nur den Build aus dem Quellcode (`docker-compose.yml` mit `build:`), IIS-Deployment per Tag und Azure.

## Entscheidung

1. **Zwei öffentliche Images** auf Docker Hub (Personal Account `joergb23`): `joergb23/learny-web` (Web + API, eine Einheit nach ADR 0003) und `joergb23/learny-mcp` (eigenständiger API-Client nach ADR 0007). Plattformen `linux/amd64` und `linux/arm64`. Tags: `MAJOR.MINOR.PATCH[-Prerelease]` und `latest` — `latest` nur bei stabilen Releases (kein Suffix, nicht als Pre-release markiert).
2. **Veröffentlichung nur per GitHub Release** (`.github/workflows/publish-docker.yml`): Version validieren (SemVer) → bauen und Integrationstests → amd64-Rauchtest (Gesundheitsprüfung, nicht root, keine Schlüssel/Datenbanken/Konfigurationen im Image) → Multi-Arch-Push → Prüfung der Plattformen und von `latest` → Docker-Hub-Beschreibung aktualisieren (eigener Schritt, `continue-on-error`, getrennte Meldung) → Installationspaket `learny-docker-<version>.zip` am Release. Externe Actions sind auf Commit-SHAs gepinnt, Zugang nur über die Secrets `DOCKERHUB_USERNAME`/`DOCKERHUB_TOKEN`. Der Workflow erzeugt keine Tags und löst keine anderen Deployments aus; der IIS-Workflow bleibt unverändert.
3. **Images:** Multi-Stage mit dem SDK nur im Build (auf der Build-Plattform, plattformunabhängig veröffentlicht), `aspnet:10.0` als Laufzeit für beide Images (der MCP-Server nutzt ASP.NET Core für HTTP), Benutzer `app` (UID 1654), keine Geheimnisse und keine Daten im Image, OCI-Labels, `wget` ausschließlich für den Healthcheck (das Basis-Image enthält weder `wget` noch `curl`). Alle Konfiguration über Umgebungsvariablen; `/app/data` ist das einzige beschreibbare Verzeichnis (Data-Protection-Schlüssel, Badge-Schlüssel samt Marker nach ADR 0023, lokale Bilder, ggf. SQLite).
4. **Installationspaket `deploy/docker/`:** `docker-compose.yml` (nur Hub-Images; Profile `db`, `mcp`, `proxy`), `.env.example` (jede Variable kommentiert), `README.md` (Anleitung und vollständige Variablentabelle), `README-web.md`/`README-mcp.md` (Docker-Hub-Beschreibungen), `mcpsettings.example.json`, nginx-Vorlage. Der bestehende Quellcode-Build (`docker-compose.yml` im Wurzelverzeichnis, `nginx/`) bleibt für Entwickler.
5. **Sichere Standards:** `AUTH_MODE` ist im Paket `oidc` (kein `auto`, ADR 0014); PostgreSQL ohne veröffentlichten Port; Web/MCP nur an `127.0.0.1` gebunden und per Reverse Proxy mit HTTPS erreichbar (die Web-App vertraut `X-Forwarded-*` von allen Quellen); Pflichtpasswort für PostgreSQL ohne Standardwert; keine Geheimnisse in YAML/Beispielen. Ein Test (`DockerConfigTests`) prüft, dass Compose, `.env.example`, Dokumentation und Code dieselben Variablen verwenden.
6. **MCP-OAuth:** Der Issuer (öffentliche MCP-URL) steht in einer eingehängten `mcpsettings.json`, weil der MCP-Server dafür keine Umgebungsvariable kennt; es wurden bewusst keine neuen Variablennamen eingeführt.

## Konsequenzen

- Ein Release auf einem **neuen** Tag erzeugt den Tag und startet dadurch den IIS-Workflow (Tag-Trigger `*`). Releases sind deshalb auf bereits vorhandenen Tags anzulegen; ein Hinweis steht im Workflow und in `deploy/docker/README.md`.
- Die Docker-Hub-Beschreibung per API braucht ein Token mit Lese-, Schreib- und Löschrechten; fehlt das Recht, bleiben die Images veröffentlicht und der Schritt meldet die Einschränkung.
- Öffentliche Images sind von jedem abrufbar; sie dürfen nie Konfiguration enthalten (durch Rauchtest und Dockerfiles abgesichert).
- Offener Sicherheitspunkt (unverändert, Prüfbericht F1): Die Anwendung selbst fällt bei `AUTH_MODE=auto`/Tippfehlern ohne OIDC-Angaben still auf `dev` zurück und unterscheidet dabei nicht nach `ASPNETCORE_ENVIRONMENT`. Das Paket vermeidet das durch den Standard `oidc`; die Härtung im Code (ADR 0014) bleibt empfohlen.

## Leitplanken für Änderungen

- Neue Umgebungsvariablen: gleichzeitig in `Infrastructure/*`, `deploy/docker/docker-compose.yml`, `deploy/docker/.env.example` und der Variablentabelle in `deploy/docker/README.md` ergänzen (der Test schlägt sonst fehl).
- Keine Geheimnisse, Datenbanken oder Schlüssel in Images; kein `latest` für Vorabversionen; Actions nur mit Commit-SHA.

## Nachtrag (ADR 0036, CVE-2026-111497)

`publish-docker.yml` baut mit `pull: true` und bricht die Veröffentlichung ab, wenn die .NET-Laufzeit der Images oder Basisimages unter 10.0.14 liegt (`tools/docker/RuntimeCheck.cs`); Basisimages bleiben bewegliche Tags, Digests stehen im Job-Summary. Siehe [0036](0036-oidc-form-post-und-https-callbacks.md).
