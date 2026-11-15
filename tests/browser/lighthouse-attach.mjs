// Lighthouse gegen einen BEREITS ANGEMELDETEN Chrome (z. B. Produktion mit OIDC-Anmeldung). Das Skript läuft nur lokal bei Ihnen; es kennt weder Zugangsdaten noch Cookies:
// Lighthouse hängt sich per DevTools-Port an den laufenden Chrome und nutzt dessen Sitzung. Der WebSocket der Blazor-Sitzung ist dadurch ebenfalls authentifiziert.
//
// Vorbereitung (einmalig):
//   1. Lighthouse-CLI außerhalb des Repositories installieren:  mkdir lh && cd lh && npm init -y && npm install lighthouse
//   2. Chrome mit frischem Profil OHNE Erweiterungen starten (Windows, PowerShell):
//        & "C:\Program Files\Google\Chrome\Application\chrome.exe" --remote-debugging-address=127.0.0.1 --remote-debugging-port=9333 --user-data-dir="$env:TEMP\lh-profil" --disable-extensions --no-first-run https://learny.brors.date
//      In diesem Chrome-Fenster normal anmelden (das Fenster offen lassen, nicht schließen, nicht in den Hintergrund-Tab wechseln).
//   3. Skript starten (Node >= 22):
//        $env:LIGHTHOUSE_CLI="<Pfad>\lh\node_modules\lighthouse\cli\index.js"; $env:LH_OUT="$env:TEMP\lh-ergebnis"
//        $env:LH_PATHS="/topics,/dashboard,/,/topics/<Themen-ID>"; node tests/browser/lighthouse-attach.mjs
//
// Variablen: LIGHTHOUSE_CLI (Pflicht), LH_OUT (Pflicht, Ordner für die Roh-JSON), LH_PATHS (Pflicht, kommagetrennt), LH_BASE (Standard https://learny.brors.date),
//            LH_PORT (Standard 9333), LH_RUNS (Standard 5).
//
// Datenschutz: Die Roh-JSON-Dateien enthalten Seiten-HTML-Ausschnitte (Elementbeschreibungen) — nicht weitergeben. Eingebettete Screenshots werden NICHT erzeugt (--skip-audits).
// Die Konsolenausgabe enthält nur Kennzahlen und Audit-Kennungen mit gekürzten Elementbeschreibungen (öffnende Tags); Themen- oder Modulnamen können darin trotzdem vorkommen — vor dem Teilen prüfen.
import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, existsSync } from 'node:fs';
import { resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const cli = process.env.LIGHTHOUSE_CLI, out = process.env.LH_OUT, paths = (process.env.LH_PATHS ?? '').split(',').map(s => s.trim()).filter(Boolean);
const base = (process.env.LH_BASE ?? 'https://learny.brors.date').replace(/\/$/, ''), port = Number(process.env.LH_PORT ?? 9333), runs = Number(process.env.LH_RUNS ?? 5);
if (!cli || !existsSync(cli) || !out || paths.length === 0) { console.error('LIGHTHOUSE_CLI (vorhandene Datei), LH_OUT und LH_PATHS setzen — siehe Kopfkommentar.'); process.exit(2); }
if (!Number.isInteger(port) || port < 1024 || port > 65535 || !Number.isInteger(runs) || runs < 1) { console.error('LH_PORT (1024–65535) und LH_RUNS (>= 1) müssen gültige Zahlen sein.'); process.exit(2); }
// Rohberichte enthalten Seitenausschnitte: nie innerhalb des Repositories ablegen (Gefahr, sie mit einzuchecken)
const repoRoot = resolve(fileURLToPath(new URL('../..', import.meta.url)));
const norm = x => (process.platform === 'win32' ? x.toLowerCase() : x) + sep;   // Windows: Groß-/Kleinschreibung (Laufwerksbuchstabe) ignorieren
if (norm(resolve(out)).startsWith(norm(repoRoot))) { console.error(`ABBRUCH: LH_OUT (${resolve(out)}) liegt im Repository. Einen Ordner außerhalb wählen (z. B. unter %TEMP%).`); process.exit(2); }
mkdirSync(out, { recursive: true });

// Prüfung: Chrome erreichbar? Erweiterungen aktiv? (verfälschen Lighthouse, siehe Hinweis „Chrome extensions negatively affected“)
let targets;
try { targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json(); }
catch { console.error(`Kein Chrome auf Port ${port} erreichbar. Chrome mit --remote-debugging-port=${port} starten (Kopfkommentar).`); process.exit(2); }
const ext = targets.filter(t => String(t.url).startsWith('chrome-extension://'));
// Chrome führt eigene Komponenten-Erweiterungen (nicht installiert, nicht abschaltbar) als Ziele; das allein ist kein Befund. Maßgeblich ist die Warnung von Lighthouse selbst (siehe unten).
console.log(`Chrome auf Port ${port} erreichbar (${ext.length} Erweiterungs-Ziele, davon meist Chrome-eigene Komponenten). Basis ${base}, ${runs} Läufe je Seite und Profil.`);
let extWarn = false, failedRuns = 0, emptyCombos = 0;

const med = v => [...v].sort((x, y) => x - y)[Math.floor(v.length / 2)];
const fm = (v, d = 0) => `${med(v).toFixed(d)} (${Math.min(...v).toFixed(d)}–${Math.max(...v).toFixed(d)})`;
for (const p of paths) for (const prof of ['mobile', 'desktop']) {
  const safe = p.replace(/[^a-z0-9]+/gi, '_').replace(/^_|_$/g, '') || 'start';
  const files = [];
  for (let i = 1; i <= runs; i++) {
    const f = `${out}/${safe}-${prof}-${i}.json`; files.push(f);
    const args = [cli, base + p, `--port=${port}`, '--disable-storage-reset', '--output=json', `--output-path=${f}`, '--quiet',
      '--only-categories=performance,accessibility,best-practices,seo', '--skip-audits=screenshot-thumbnails,final-screenshot,full-page-screenshot'];
    if (prof === 'desktop') args.push('--preset=desktop');
    const r = spawnSync('node', args, { encoding: 'utf8' });
    if (r.status !== 0) failedRuns++;
    if (r.status !== 0) console.log(`  Lauf ${i} (${p}, ${prof}) fehlgeschlagen: ${(r.stderr || r.stdout || '').split('\n')[0].slice(0, 160)}`);
  }
  const rs = files.filter(existsSync).map(f => JSON.parse(readFileSync(f, 'utf8')));
  if (rs.length === 0) { console.log(`${p} ${prof}: keine Ergebnisse`); emptyCombos++; continue; }
  const cat = k => rs.map(r => Math.round(r.categories[k].score * 100)), au = id => rs.map(r => r.audits[id]?.numericValue ?? 0);
  const warn = rs.flatMap(r => r.runWarnings ?? []).filter((w, i, a) => a.indexOf(w) === i);
  console.log(`\n${p} | ${prof} | ${rs.length} Läufe (Median, min–max)`);
  console.log(`  Performance ${fm(cat('performance'))}  Accessibility ${fm(cat('accessibility'))}  Best Practices ${fm(cat('best-practices'))}  SEO ${fm(cat('seo'))}`);
  console.log(`  FCP ${fm(au('first-contentful-paint'))} ms  LCP ${fm(au('largest-contentful-paint'))} ms  TBT ${fm(au('total-blocking-time'))} ms  CLS ${fm(au('cumulative-layout-shift'), 3)}  Version ${rs[0].lighthouseVersion}`);
  if (warn.length) console.log(`  Lighthouse-Warnungen: ${warn.map(w => w.slice(0, 140)).join(' | ')}`);
  if (warn.some(w => /extension/i.test(w))) { extWarn = true; console.log('  >>> ACHTUNG: Lighthouse meldet Browser-Erweiterungen — Performance-Werte sind nicht verwertbar. Mit frischem Profil und ohne Erweiterungen wiederholen.'); }
  // Auffällige Audits (Lauf 1) mit betroffenen Elementen (gekürzt)
  for (const a of Object.values(rs[0].audits).filter(x => x.score !== null && x.score < 0.9 && !['informative', 'notApplicable', 'manual'].includes(x.scoreDisplayMode))) {
    const items = (Array.isArray(a.details?.items) ? a.details.items : []).filter(i => i && i.node).slice(0, 4).map(i => (i.node.snippet ?? '').replace(/\s+/g, ' ').slice(0, 110));
    console.log(`  - ${a.id} (${a.score})${items.length ? ': ' + items.join(' || ') : ''}`);
  }
}
if (extWarn) console.log('\nERGEBNIS UNGÜLTIG für Performance: Erweiterungen erkannt (siehe >>> oben).');
console.log(`\nRoh-JSON liegt in ${out} (nicht weitergeben). Weitergeben: die obige Zusammenfassung.`);
// Exit-Codes: 0 = alle Läufe gültig; 1 = Läufe fehlgeschlagen, Seiten ohne Ergebnis oder Erweiterungswarnung (Performance-Werte ungültig); 2 = Aufruf/Voraussetzung falsch
if (failedRuns > 0 || emptyCombos > 0 || extWarn) { console.log(`
Exit 1: ${failedRuns} fehlgeschlagene Läufe, ${emptyCombos} Seite(n) ohne Ergebnis${extWarn ? ', Erweiterungen erkannt' : ''}.`); process.exit(1); }
