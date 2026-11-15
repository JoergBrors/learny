// Persönliche Avatar-Galerie, Export und Import (ADR 0057): Varianten dauerhaft speichern, Gruppen, aktivieren, umbenennen, Favorit, Filter, Löschen mit Bestätigung,
// ZIP-Export (mit/ohne Passwort), Import in ein anderes Konto (falsches Passwort, Vorschau, Duplikate), Isolation, Mobil/Dark Mode.
// Voraussetzung: Instanz mit KI-Bild-Stub (siehe avatar-image.mjs: node tests/browser/stub-image-api.mjs; OPENAI_API_KEY=stub-key OPENAI_BASE_URL=http://127.0.0.1:5298/v1).
// Prüft nicht die Bildqualität eines echten Modells. Die ZIP-Dateien werden – falls 7-Zip vorhanden – zusätzlich mit 7-Zip getestet.
import { A, B, newPage, api, ok, sleep, finish } from './lib.mjs';
import { writeFileSync, readFileSync, readdirSync, mkdtempSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

const SHOT = process.env.SHOT_DIR;
const shot = async (p, name) => { if (!SHOT) return; const r = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(`${SHOT}/${name}.png`, Buffer.from(r.result.data, 'base64')); };
const until = async (p, expr, ms = 10000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.ev(expr)) return true; await sleep(200); } return false; };
const live = async p => { const t0 = Date.now(); while (Date.now() - t0 < 15000) { await p.click('Motivierend'); if (await p.ev(`document.querySelector('.avatar-preview .chip-active')?.innerText.trim()==='Motivierend'`)) return true; await sleep(300); } return false; };
const inScope = (p, scope, label, nth = 0) => p.ev(`(()=>{const e=[...document.querySelectorAll(${JSON.stringify(scope + ' button')})].filter(x=>x.innerText&&x.innerText.trim().startsWith(${JSON.stringify(label)})&&!x.disabled)[${nth}];if(!e)return false;e.click();return true})()`);
const count = (p, sel) => p.ev(`document.querySelectorAll(${JSON.stringify(sel)}).length`);
const SEVEN = ['C:\\Program Files\\7-Zip\\7z.exe', '/usr/bin/7z', '/usr/bin/7zz'].find(existsSync);
const sevenTest = (file, pw) => { if (!SEVEN) return null; const args = ['t', file]; if (pw) args.push('-p' + pw); return spawnSync(SEVEN, args, { encoding: 'utf8' }).status === 0; };

const dl = mkdtempSync(join(tmpdir(), 'learny-gallery-dl-'));
const newest = () => { const f = readdirSync(dl).filter(x => x.endsWith('.zip')).map(x => join(dl, x)).sort((a, b) => b.localeCompare(a)); return f[0]; };

try {
  const user = 'avgal-' + Date.now();
  const a = await newPage(`${A}/auth/login?devUser=${user}`); await sleep(2500);
  await a.send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: dl, eventsEnabled: false });
  await a.go(`${A}/profile`);
  ok(await a.waitText('Meine Avatar-Galerie'), 'Profil: Bereich „Meine Avatar-Galerie“ ist vorhanden');
  ok(await live(a), 'Profilseite ist interaktiv');
  ok(await a.waitText('Noch keine gespeicherten Avatare'), 'Leere Galerie: klarer Leerzustand');

  // drei Varianten erzeugen und gemeinsam speichern
  const gen = async text => {
    await a.click('Textbeschreibung'); await a.type('.avatar-ai textarea', text);
    const t0 = Date.now(); let clicked = false;
    while (!clicked && Date.now() - t0 < 6000) { clicked = await inScope(a, '.avatar-ai', 'Avatar erstellen') || await inScope(a, '.avatar-ai', 'Neue Variante erstellen'); if (!clicked) await sleep(150); }
  };
  await gen('graue Haare, Vollbart'); await until(a, `document.querySelectorAll('.avatar-variant').length===1`);
  await a.click('3D-Cartoon'); await gen('rote Locken, Brille'); await until(a, `document.querySelectorAll('.avatar-variant').length===2`);
  await a.click('Klassischer Comic'); await gen('blonder Dutt'); await until(a, `document.querySelectorAll('.avatar-variant').length===3`);
  ok(await count(a, '.avatar-variant') === 3, 'Drei Vorschläge liegen im Generator');
  await a.click('Alle in Galerie speichern');
  ok(await until(a, `document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing)').length===3`) && await count(a, '.avatar-variant') === 0, 'Alle drei Vorschläge sind dauerhaft in der Galerie (Vorschläge leer)');
  ok(await count(a, '.gallery-group') === 1 && await a.ev(`document.querySelector('.gallery-group-head')?.innerText.includes('3 Varianten')`), 'Die Varianten sind als eine Gruppe zusammengefasst');
  ok(await a.ev(`document.querySelector('.avatar-preview .avatar-initials')!==null || !document.querySelector('.avatar-preview img.avatar-img[src*="gal"]')`), 'Speichern in der Galerie ändert den Profilavatar nicht');
  await shot(a, 'gallery-desktop-light');

  // Neuladen: dauerhaft
  await a.go(`${A}/profile`); await live(a);
  ok(await until(a, `document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing)').length===3`), 'Nach Neuladen: die drei Einträge sind noch da (dauerhaft gespeichert)');

  // aktivieren
  await inScope(a, '#avatar-gallery', 'Verwenden', 1);
  ok(await until(a, `document.querySelectorAll('.gallery-tile-active').length===1`) && await a.ev(`document.querySelector('.gallery-tile-active')?.innerText.includes('Aktiver Profilavatar')`), 'Verwenden: genau ein Eintrag ist als aktiver Profilavatar gekennzeichnet');
  ok(await until(a, `!!document.querySelector('.avatar-preview img.avatar-img[src^="/api/me/avatar-image?v="]')`) && (await api(a, '/api/me/avatar-image?nc=' + Date.now())).s === 200, 'Der aktive Galerie-Avatar wird als Profilbild angezeigt und ausgeliefert');
  ok(await a.ev(`[...document.querySelectorAll('.gallery-tile-active button')].some(b=>b.innerText.trim().startsWith('Löschen')&&b.disabled)`), 'Der aktive Avatar kann nicht gelöscht werden (Schaltfläche gesperrt)');

  // umbenennen, Favorit, Filter
  await inScope(a, '#avatar-gallery .gallery-tile:not(.gallery-tile-missing)', 'Umbenennen');
  await until(a, `!!document.querySelector('#avatar-gallery .gallery-tile:not(.gallery-tile-missing) input.input')`);
  await a.type('#avatar-gallery .gallery-tile:not(.gallery-tile-missing) input.input', 'Mein Held'); await sleep(300);
  await inScope(a, '#avatar-gallery', 'Speichern');
  ok(await until(a, `[...document.querySelectorAll('.gallery-name')].some(x=>x.innerText.trim()==='Mein Held')`), 'Umbenennen funktioniert');
  await a.ev(`document.querySelector('#avatar-gallery .gallery-tile:not(.gallery-tile-missing) button[aria-pressed]').click()`);
  ok(await until(a, `document.querySelectorAll('#avatar-gallery button[aria-pressed="true"]').length===1`), 'Favorit setzen');
  await a.ev(`(()=>{const c=[...document.querySelectorAll('#avatar-gallery label.pick')].find(l=>l.innerText.includes('Nur Favoriten')).querySelector('input');c.click()})()`);
  ok(await until(a, `document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing)').length===1`), 'Filter „Nur Favoriten“ zeigt nur den Favoriten');
  await a.ev(`(()=>{const c=[...document.querySelectorAll('#avatar-gallery label.pick')].find(l=>l.innerText.includes('Nur Favoriten')).querySelector('input');c.click()})()`);
  await until(a, `document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing)').length===3`);

  // Löschen mit Bestätigung (nicht aktiv)
  await a.ev(`(()=>{const t=[...document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing)')].find(x=>!x.classList.contains('gallery-tile-active'));[...t.querySelectorAll('button')].find(b=>b.innerText.trim().startsWith('Löschen')).click()})()`);
  ok(await a.waitText('endgültig löschen'), 'Löschen verlangt eine Bestätigung');
  await inScope(a, '#avatar-gallery', 'Abbrechen');
  ok(await count(a, '.gallery-tile:not(.gallery-tile-missing)') === 3, 'Abbrechen löscht nichts');
  await a.ev(`(()=>{const t=[...document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing)')].find(x=>!x.classList.contains('gallery-tile-active'));[...t.querySelectorAll('button')].find(b=>b.innerText.trim().startsWith('Löschen')).click()})()`);
  await inScope(a, '#avatar-gallery', 'Ja, löschen');
  ok(await until(a, `document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing)').length===2`), 'Nach Bestätigung ist der Eintrag gelöscht, der aktive Avatar bleibt');
  // zurück auf 3 für den Export: neue Variante erzeugen und speichern
  await a.click('Klassischer Comic'); await gen('Mann mit Glatze'); await until(a, `document.querySelectorAll('.avatar-variant').length===1`);
  await a.click('In Galerie speichern');
  ok(await until(a, `document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing)').length===3`), 'Einzelnen Vorschlag in die Galerie speichern (andere Einträge bleiben)');

  // Export ohne Passwort
  await inScope(a, '#avatar-gallery', 'Gesamte Galerie exportieren');
  ok(await until(a, `!!document.body.innerText.includes('Archiv „learny-avatar-galerie')`, 15000), 'Export: Meldung über das erstellte Archiv');
  await sleep(800);
  const plainZip = newest();
  ok(!!plainZip && readFileSync(plainZip).subarray(0, 2).toString() === 'PK' && (sevenTest(plainZip) ?? true), 'Export: ZIP-Datei wurde heruntergeladen' + (SEVEN ? ' und besteht den 7-Zip-Test' : ' (7-Zip nicht vorhanden, nur Signatur geprüft)'));
  rmSync(plainZip);

  // Export mit Passwort
  await a.ev(`(()=>{const c=[...document.querySelectorAll('#avatar-gallery label.pick')].find(l=>l.innerText.includes('Passwort schützen')).querySelector('input');c.click()})()`);
  await until(a, `document.querySelectorAll('#avatar-gallery input[type=password]').length===2`);
  await a.type('#avatar-gallery .gallery-pw label:nth-child(1) input', 'abc'); await sleep(300);
  ok(await a.waitText('Schwach'), 'Passwortschutz: Hinweis bei schwachem Passwort');
  ok(await a.waitText('nicht wiederhergestellt'), 'Passwortschutz: Hinweis, dass ein vergessenes Passwort nicht wiederhergestellt werden kann');
  await inScope(a, '#avatar-gallery', 'Gesamte Galerie exportieren');
  ok(await a.waitText('zu kurz'), 'Passwortschutz: zu kurzes Passwort wird abgelehnt (kein Export)');
  const PW = 'Korrekt-Pferd-Batterie-9';
  await a.type('#avatar-gallery .gallery-pw label:nth-child(1) input', PW); await a.type('#avatar-gallery .gallery-pw label:nth-child(2) input', PW + 'x'); await sleep(300);
  await inScope(a, '#avatar-gallery', 'Gesamte Galerie exportieren');
  ok(await a.waitText('stimmen nicht überein'), 'Passwortschutz: abweichende Bestätigung wird abgelehnt');
  await a.type('#avatar-gallery .gallery-pw label:nth-child(2) input', PW); await sleep(300);
  await inScope(a, '#avatar-gallery', 'Gesamte Galerie exportieren');
  ok(await until(a, `document.body.innerText.includes('AES-256-geschützt')`, 15000), 'Export mit Passwort: Meldung (Passwort wurde nicht gespeichert)');
  await sleep(800);
  const encZip = newest();
  ok(!!encZip && (sevenTest(encZip, PW) ?? true) && (sevenTest(encZip, 'falsch-falsch-1') !== true), 'Export mit Passwort: 7-Zip öffnet das AES-Archiv mit dem Passwort, aber nicht mit einem falschen' + (SEVEN ? '' : ' (7-Zip nicht vorhanden – nicht geprüft)'));
  ok(await a.ev(`[...document.querySelectorAll('#avatar-gallery input[type=password]')].every(i=>i.value==='')`) && !(await a.text()).includes(PW), 'Passwort wird nach dem Export nicht im Formular/Seitentext behalten');
  const encPath = join(dl, 'enc.zip'); writeFileSync(encPath, readFileSync(encZip));

  // Import in ein anderes Konto (anderer Host = andere Sitzung)
  const b = await newPage(`${B}/auth/login?devUser=${user}-import`); await sleep(2500);
  await b.go(`${B}/profile`); await live(b);
  ok(await b.waitText('Noch keine gespeicherten Avatare'), 'Zweites Konto: eigene, leere Galerie (keine fremden Avatare)');
  const pick = async (p, file) => { const d = await p.send('DOM.getDocument'); const q = await p.send('DOM.querySelector', { nodeId: d.result.root.nodeId, selector: '#avatar-gallery .gallery-import-slot input[type=file], #avatar-gallery input[type=file]' }); await p.send('DOM.setFileInputFiles', { files: [file], nodeId: q.result.nodeId }); };
  await pick(b, encPath);
  ok(await b.waitText('passwortgeschützt', 8000), 'Import: verschlüsseltes Archiv fordert das Passwort an');
  await b.type('#avatar-gallery .gallery-pw input', 'falsches-Passwort-1'); await sleep(300);
  await inScope(b, '#avatar-gallery', 'Archiv öffnen');
  ok(await b.waitText('Passwort ist falsch', 8000) && await count(b, '.gallery-tile:not(.gallery-tile-missing)') === 0 && await count(b, '.gallery-import') === 0, 'Falsches Passwort: verständliche Meldung, keine Vorschau, nichts importiert');
  await b.type('#avatar-gallery .gallery-pw input', PW); await sleep(300);
  await inScope(b, '#avatar-gallery', 'Archiv öffnen');
  ok(await until(b, `!!document.querySelector('.gallery-import')`, 8000) && await count(b, '.gallery-import-list li') === 3, 'Richtiges Passwort: Importvorschau mit allen 3 Avataren');
  ok(await count(b, '.gallery-tile:not(.gallery-tile-missing)') === 0, 'Vor der Bestätigung wird nichts importiert');
  await shot(b, 'gallery-import-preview');
  await inScope(b, '#avatar-gallery', 'Import bestätigen');
  ok(await until(b, `document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing)').length===3`, 8000) && await count(b, '.gallery-group') === 2 && await a.ev("document.querySelectorAll('.gallery-group').length") === 2, 'Import bestätigt: alle 3 Avatare in den ursprünglichen 2 Gruppen (2+1) in der Galerie');
  ok(await b.ev(`document.querySelector('.avatar-preview .avatar-initials')!==null`), 'Import ändert den Profilavatar des Zielkontos nicht');
  await b.go(`${B}/profile`); await live(b);
  ok(await until(b, `document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing)').length===3`), 'Nach Neuladen: importierte Avatare sind dauerhaft gespeichert');
  // Duplikate
  await pick(b, encPath); await b.waitText('passwortgeschützt', 8000);
  await b.type('#avatar-gallery .gallery-pw input', PW); await sleep(300); await inScope(b, '#avatar-gallery', 'Archiv öffnen'); await until(b, `!!document.querySelector('.gallery-import')`);
  ok((await b.text()).includes('schon vorhanden'), 'Import: Duplikate werden in der Vorschau gekennzeichnet');
  await inScope(b, '#avatar-gallery', 'Import bestätigen');
  ok(await b.waitText('Duplikat(e) übersprungen', 8000) && await count(b, '.gallery-tile:not(.gallery-tile-missing)') === 3, 'Duplikate überspringen: nichts doppelt');

  // Isolation
  const ids = await b.ev(`[...document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing) img.avatar-img')].map(i=>i.src.match(/gallery\\/([0-9a-f]{32})\\//)?.[1])`);
  const o = await newPage(`${A}/auth/login?devUser=${user}-fremd`); await sleep(2500);
  const foreign = await o.ev(`fetch('/api/me/avatar-gallery/${ids[0]}/image').then(r=>r.status)`);
  ok(ids.length === 3 && ids.every(x => /^[0-9a-f]{32}$/.test(x ?? '')) && foreign === 404, 'Isolation: ein fremdes Konto erhält für die (erratene) Bildkennung 404');

  // Mobil + Dark Mode
  await b.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'dark' }] });
  await b.send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 2, mobile: true });
  await b.go(`${B}/profile`); await live(b); await until(b, `document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing)').length===3`);
  await b.ev(`document.querySelector('#avatar-gallery')?.scrollIntoView()`); await sleep(500);
  ok(await b.ev(`document.documentElement.scrollWidth <= innerWidth + 1 && [...document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing)')].every(t=>{const r=t.getBoundingClientRect();return r.left>=0&&r.right<=innerWidth+1})`), 'Mobil (390 px) und Dark Mode: Galerie ohne horizontales Scrollen, alle Kacheln im Bild');
  await shot(b, 'gallery-mobile-dark');

  const errs = [a, b, o].flatMap(p => p.errors); ok(errs.length === 0, `Keine JavaScript-/Konsolenfehler (${errs.length}) ${errs.slice(0, 3).join(' | ')}`);
} catch (e) { console.log('SKRIPTFEHLER', e); await finish(1); }
try { rmSync(dl, { recursive: true, force: true }); } catch { /* egal */ }
await finish();
