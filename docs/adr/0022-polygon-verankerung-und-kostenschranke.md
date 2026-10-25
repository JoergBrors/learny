# 0022 — Polygon-Verankerung, Merkle-Bündelung und harte Kostenschranke

- **Status:** Akzeptiert — Pipeline umgesetzt; **echtes Signieren/Senden bewusst offen** (Entscheidung nötig, siehe unten)
- **Datum:** 2026-10-26
- **Ergänzt:** [0021](0021-zertifikate-und-open-badges.md), [0009](0009-deployment-wege.md) (Konfiguration), [0005](0005-authentifizierung.md), [0013](0013-mcp-benutzergebundene-anmeldung.md)
- **Bezug:** [0002](0002-keine-nuget-pakete.md)

## Entscheidung

1. **Netzwerke:** Entwicklung/Test = Polygon **Amoy** (Chain-ID 80002, Test-POL, keine echten Gebühren); Produktion = Polygon PoS **Mainnet** (137). RPC-Adressen sind konfigurierbar (`BLOCKCHAIN_RPC_URL_AMOY/MAINNET`), nie fest verdrahtet. Jeder Nachweis speichert Netzwerk und Chain-ID bei der Ausstellung; ein Netzwerkwechsel ändert bestehende Nachweise nicht und verankert sie nicht im neuen Netzwerk.
2. **Betriebsmodi:** `BLOCKCHAIN_ENABLED=false` (nur signierte Badges), Testnet (Standard: `BLOCKCHAIN_NETWORK=polygon-amoy`, `BLOCKCHAIN_AUTO_ANCHOR=true`), Mainnet. **Mainnet erfordert beides:** `BLOCKCHAIN_NETWORK=polygon-mainnet` + `BLOCKCHAIN_MAINNET_ENABLED=true` **und** eine ausdrückliche administrative Freigabe (`POST /api/admin/blockchain/mainnet-approval` mit Bestätigungstext). Die Freigabe gilt nur für die dann konfigurierte Wallet, ist widerrufbar und protokolliert.
   Die ASP.NET-Umgebung schaltet nie auf Mainnet. `BLOCKCHAIN_SIMULATE=true` simuliert lokal (Netzwerk „simulation“, überall als „simuliert“ gekennzeichnet, nie mit Mainnet).
3. **Anker ohne Smart Contract:** Eine Nullwert-Transaktion der Wallet an sich selbst mit den Daten `LCv1` + Merkle-Wurzel (32 Byte). Es gibt keine personenbezogenen Daten, keine NFTs/Token und keine Vertragskosten. Prüfung: Transaktion per RPC lesen (Absender = Wallet, Daten = erwarteter Anker, Receipt-Status, Bestätigungen, Chain-ID).
4. **Merkle-Bündelung:** Blatt = SHA-256(0x00 ‖ Badge-Hash), Knoten = SHA-256(0x01 ‖ links ‖ rechts), ungerader Knoten wird unverändert hochgereicht; Reihenfolge = Ausstellungszeit/ID. Jeder Nachweis erhält einen eigenen Pfad (`certificates.merkle_proof_json`), jeder Batch eine eindeutige Wurzel (Unique-Index je Netzwerk → gleiche Menge = gleicher Batch, nie eine zweite Verankerung).
   Fällig wird ein Batch bei `BLOCKCHAIN_BATCH_MAX_SIZE` (100) Nachweisen oder nach `BLOCKCHAIN_BATCH_MAX_WAIT_MINUTES` (60); beides ist per Admin nur verkleinerbar.
5. **Harte Kostenschranke** (`AnchorCostGuard`): Kosten = Gas-Limit (Schätzung + 20 %) × (Gaspreis × 1,5) × (POL/EUR-Kurs × 1,25); Limit je Nachweis = min(Admin-/Umgebungswert, **0,001 EUR**) — Gesamtkosten des Batches geteilt durch die Zahl der Nachweise. Gesperrt (nicht gesendet) wird bei: unbekanntem Gas oder Gaspreis, fehlendem oder veraltetem Kurs (> 15 min), überschrittenem Limit, leerem Batch.
   Die Prüfung läuft **zweimal mit frischen Daten** (vor und unmittelbar vor dem Signieren); die Transaktion übernimmt die engere Grenze als `maxFeePerGas` und das Gas-Limit — mehr als diese Obergrenze kann nie bezahlt werden. Wiederholungen rechnen jedes Mal neu; sie senden bei erneutem Verstoß wieder nichts und eskalieren nie. Es gibt keinen Ersatzweg.
   Zusätzlich: kein Senden ohne Mainnet-Freigabe, ohne Signierer, bei falscher Chain-ID oder zu geringem Wallet-Guthaben. Gesperrte Nachweise bleiben „ausstehend/Kostenlimit überschritten“, das Badge ist unberührt.
6. **Idempotenz und Wiederholung:** Die signierte Rohtransaktion wird **vor** dem Senden gespeichert; Wiederholungen senden dieselbe Transaktion (gleicher Hash) erneut. Nach 90 Minuten ohne Aufnahme wird der Batch „fehlgeschlagen“ (Administrator kann erneut einplanen; volle Kostenprüfung). Parallele Verarbeitung ist serialisiert. Finalität: erst nach `BLOCKCHAIN_CONFIRMATIONS` (30) Bestätigungen **und** erfolgreicher Datenprüfung gilt ein Nachweis als „Blockchain-bestätigt“.
7. **Kostenerfassung je Batch:** Gas, effektiver Gaspreis, Gebühr (wei), Kurs und Zeitpunkt, geschätzte und tatsächliche EUR-Kosten, Kosten je Nachweis, geschätzte Ersparnis durch Bündelung (nur mit belastbaren Daten). Testnet/Simulation weisen reale Ausgaben mit **0 EUR** aus; hypothetische Mainnet-Kosten stehen getrennt als „simuliert“.
8. **Signierung — offene Entscheidung:** EVM-Transaktionen brauchen Keccak-256 (nicht SHA3-256) und secp256k1-Signaturen; beides fehlt in der .NET-Basisbibliothek (secp256k1 unter Windows gar nicht). Eigene Kryptografie ist ausgeschlossen. Die Anwendung enthält daher `IAnchorSigner` mit der Standardimplementierung „nicht konfiguriert“ — der private Schlüssel gehört nie in diese Anwendung, solange nichts gewählt ist. Der Pseudo-Signierer gilt nur für Simulation/Tests (lokaler RPC erzwungen). **Alternativen (bitte entscheiden):**
   (a) NuGet-Paket (z. B. Nethereum) per neuer ADR freigeben — geringster Aufwand, Schlüssel per Secret-Verwaltung; (b) Azure Key Vault / Managed HSM mit EC-Schlüssel `SECP256K1` signiert den Transaktions-Hash, Keccak-256 und RLP (testbar mit Standardvektoren) lokal — Schlüssel verlässt den Tresor nie; (c) externer Signaturdienst.
   Wallet: eigene technische Wallet je Netzwerk (Mainnet getrennt vom Testnet), Adresse in `BLOCKCHAIN_WALLET_ADDRESS`, Guthaben wird überwacht; ohne Guthaben wird nicht gesendet.
9. **Preisquelle:** konfigurierbare HTTPS-Quelle (`BLOCKCHAIN_PRICE_URL`, JSON-Pfad), kein fest hinterlegter Kurs; schlägt der Abruf fehl, gilt der letzte Kurs mit seinem Alter — veraltet = gesperrt.

## Praktische Umsetzbarkeit des 0,001-EUR-Limits (Rechnung, nicht gemessen)

Ein Anker (36 Byte Daten) braucht etwa 21 576 Gas. Bei 30 Gwei sind das 0,00065 POL; bei 0,20 EUR/POL ≈ 0,00013 EUR, mit den Sicherheitsaufschlägen (1,5 × 1,25) ≈ 0,00024 EUR — also **unter** 0,001 EUR, auch ohne Bündelung. Kritisch wird es erst bei sehr hohen Gaspreisen (auf Polygon Mainnet gilt eine Mindest-Priority-Fee von 25 Gwei) oder sehr hohem POL-Kurs;
die Schranke sperrt dann und die Bündelung verteilt die Fixkosten (100 Nachweise ⇒ ein Hundertstel je Nachweis). Reale Messwerte von Amoy liegen nicht vor, weil das Senden noch nicht freigeschaltet ist. Die Schranke garantiert die Grenze nur, solange der Kurs sich zwischen Berechnung und Abrechnung um weniger als den Aufschlag (25 %) bewegt; eine absolute EUR-Garantie ist bei schwankenden Kursen nicht möglich und wird nicht behauptet. RPC-/Infrastrukturkosten sind separat.

## Betrieb ohne Blockchain / Ausfälle

Bei `BLOCKCHAIN_ENABLED=false`, RPC-Ausfall, fehlendem Guthaben oder fehlender Preisquelle bleiben Badges signiert und prüfbar; Nachweise warten (Status „Verankerung ausstehend“ bzw. „Kostenlimit überschritten“) und werden später verarbeitet. Nichts geht verloren.

## Offene Punkte

- **Signierlösung entscheiden (Punkt 8);** erst danach ist echtes Senden an Amoy/Mainnet möglich. Mainnet bleibt bis dahin und bis zur ausdrücklichen Freigabe deaktiviert; es wurden keine Transaktionen gesendet.
- Oberfläche (Admin-Blockchain-Seiten, Kostenübersicht, Nutzer-/Auditor-Seiten, Navigation) fehlt noch; die Funktionen stehen als REST-API zur Verfügung (`/api/admin/blockchain`, `/api/certificates`, `/api/audit`, `/api/public/…`).
- Mehrere Wallets/Nonce-Verwaltung für parallele Sender; Key-Vault-Anbindung.
