// Fünf echte Stimmungsbilder je KI-Figur (ADR 0058): Erzeugen mit Kostenbestätigung, Figur mit fünf Kacheln in der Galerie, Wechsel der Stimmung im Profil ohne KI-Aufruf,
// Hinweis bei fehlenden Stimmungen, Teilfehler und gezieltes Nachholen, Export (ZIP, AES-256, 7-Zip-Test falls vorhanden) und Reimport in ein zweites Konto, Mobil/Dark Mode.
// Voraussetzung: Instanz mit KI-Bild-Stub (siehe avatar-image.mjs). Prüft NICHT die Bildqualität/Identitätskonsistenz eines echten Modells.
import { A, B, newPage, api, ok, sleep, finish } from './lib.mjs';
import { writeFileSync, readFileSync, readdirSync, mkdtempSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

const STUB = process.env.STUB_URL ?? 'http://127.0.0.1:5298';
const SHOT = process.env.SHOT_DIR;
const shot = async (p, name) => { if (!SHOT) return; const r = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(`${SHOT}/${name}.png`, Buffer.from(r.result.data, 'base64')); };
const until = async (p, expr, ms = 15000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.ev(expr)) return true; await sleep(200); } return false; };
const live = async p => { const t0 = Date.now(); while (Date.now() - t0 < 15000) { await p.click('Motivierend'); if (await p.ev(`document.querySelector('.avatar-preview .chip-active')?.innerText.trim()==='Motivierend'`)) return true; await sleep(300); } return false; };
const inScope = (p, scope, label, nth = 0) => p.ev(`(()=>{const e=[...document.querySelectorAll(${JSON.stringify(scope + ' button')})].filter(x=>x.innerText&&x.innerText.trim().startsWith(${JSON.stringify(label)})&&!x.disabled)[${nth}];if(!e)return false;e.click();return true})()`);
const count = (p, sel) => p.ev(`document.querySelectorAll(${JSON.stringify(sel)}).length`);
const stubLog = async () => JSON.parse(await (await fetch(STUB + '/__log')).text());
const hashOf = (p, url) => p.ev(`fetch(${JSON.stringify(url)}).then(r=>r.arrayBuffer()).then(async b=>[...new Uint8Array(await crypto.subtle.digest('SHA-256', b))].map(x=>x.toString(16).padStart(2,'0')).join('').slice(0,16))`);
const SEVEN = ['C:\\Program Files\\7-Zip\\7z.exe', '/usr/bin/7z', '/usr/bin/7zz'].find(existsSync);
const dl = mkdtempSync(join(tmpdir(), 'learny-moods-dl-'));
const MOODS = ['neutral', 'happy', 'motivating', 'success', 'encouraging'];

try {
  const user = 'avmood-' + Date.now();
  const logBase = (await stubLog()).length;
  const a = await newPage(`${A}/auth/login?devUser=${user}`); await sleep(2500);
  await a.send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: dl, eventsEnabled: false });
  await a.go(`${A}/profile`); await live(a);

  // Hauptavatar erzeugen, speichern und die Stimmungen planen (nur Bestätigungsdialog, noch kein Aufruf)
  await a.click('Textbeschreibung'); await a.type('.avatar-ai textarea', 'graue Haare, Vollbart');
  const t0 = Date.now(); let clicked = false; while (!clicked && Date.now() - t0 < 6000) { clicked = await inScope(a, '.avatar-ai', 'Avatar erstellen'); if (!clicked) await sleep(150); }
  ok(await until(a, `document.querySelectorAll('.avatar-variant').length===1`), 'Hauptavatar (neutral) wurde erzeugt');
  const callsAfterMain = (await stubLog()).length - logBase;
  await a.click('Speichern + Stimmungen');
  ok(await a.waitText('kostenpflichtig', 8000) && await a.waitText('4 Bild(er)'), 'Bestätigungsdialog nennt Anzahl (4 Bilder), Kosten und Tageslimit, bevor etwas erzeugt wird');
  ok((await stubLog()).length - logBase === callsAfterMain, 'Vor der Bestätigung wird kein einziger weiterer KI-Aufruf ausgelöst');
  ok(await count(a, '.gallery-tile-missing') === 4 && await a.ev(`document.querySelector('.gallery-figure-head')?.innerText.includes('1 von 5 Stimmungen')&&document.querySelector('.gallery-figure-head')?.innerText.includes('unvollständig')`), 'Die Figur ist als unvollständig erkennbar (1 von 5, vier Platzhalter)');

  // Erzeugen nach ausdrücklicher Bestätigung (Teilfehler und Limits sind in den Integrationstests abgedeckt)
  await inScope(a, '#avatar-gallery', 'Jetzt erzeugen');
  ok(await a.waitText('Ergebnis der Erzeugung', 30000), 'Nach der Bestätigung erscheint ein Ergebnisbericht');
  await until(a, `document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing)').length===5`, 30000);
  ok(await count(a, '.gallery-tile:not(.gallery-tile-missing)') === 5 && await count(a, '.gallery-tile-missing') === 0, 'Alle fünf Stimmungen liegen als eigene Kacheln in der Galerie');
  const moodsLog = (await stubLog()).slice(logBase).filter(l => l.mood).map(l => l.mood);
  ok(moodsLog.join(',') === 'happy,motivating,success,encouraging', 'Je Stimmung genau ein Bildauftrag mit dem passenden Ausdruck (Reihenfolge: ' + moodsLog.join(', ') + ')');
  ok((await stubLog()).slice(logBase).filter(l => l.mood).every(l => l.path.endsWith('/images/edits') && l.photo), 'Stimmungen werden über den Edit-Endpunkt mit dem Hauptbild als Referenz erzeugt');
  const labels = await a.ev(`[...document.querySelectorAll('.gallery-tile .badge-mood')].map(x=>x.innerText.trim()).sort().join(',')`);
  ok(labels === ['Erfolgreich', 'Ermutigend', 'Freundlich', 'Motivierend', 'Neutral'].join(','), 'Jede Kachel nennt ihre Stimmung eindeutig (' + labels + ')');
  const srcs = await a.ev(`[...document.querySelectorAll('.gallery-tile img.avatar-img')].map(i=>i.getAttribute('src'))`);
  const hashes = []; for (const s of srcs) hashes.push(await hashOf(a, s));
  ok(new Set(hashes).size === 5, 'Fünf unterschiedliche Bilddateien');
  ok(await a.ev(`document.querySelector('.gallery-figure-head')?.innerText.includes('5 von 5 Stimmungen')&&!document.querySelector('.gallery-figure-head')?.innerText.includes('unvollständig')`), 'Die Figur ist vollständig (5 von 5)');
  await shot(a, 'moods-gallery-desktop');

  // Verwenden + Stimmungswechsel im Profil: jedes Bild passend, ohne neuen KI-Aufruf
  await inScope(a, '#avatar-gallery .gallery-tile', 'Verwenden');
  await until(a, `!!document.querySelector('.avatar-preview img.avatar-img')`);
  const callsBeforeSwitch = (await stubLog()).length;
  const seen = {};
  for (const [id, label] of [['neutral', 'Neutral'], ['happy', 'Freundlich'], ['motivating', 'Motivierend'], ['success', 'Erfolgreich'], ['encouraging', 'Ermutigend']]) {
    await a.click(label); await until(a, `document.querySelector('.avatar-preview .chip-active')?.innerText.trim()==='${label}'`);
    await sleep(500);
    const src = await a.ev(`document.querySelector('.avatar-preview img.avatar-img')?.getAttribute('src')`);
    seen[id] = { src, hash: await hashOf(a, src) };
  }
  ok(MOODS.every(m => seen[m].hash) && new Set(MOODS.map(m => seen[m].hash)).size === 5, 'Die fünf Stimmungsschaltflächen zeigen fünf verschiedene, zugeordnete Bilder (' + MOODS.map(m => seen[m].hash.slice(0, 6)).join(' ') + ')');
  ok(MOODS.filter(m => m !== 'neutral').every(m => seen[m].src.includes('mood=' + m)), 'Die Bildadresse trägt die gewählte Stimmung');
  ok((await stubLog()).length === callsBeforeSwitch, 'Der Wechsel der Stimmungen löst keinen KI-Aufruf aus');
  ok(await a.ev(`!document.querySelector('.avatar-mood-hint')`), 'Bei vollständiger Figur erscheint kein Ersatzhinweis');

  // Figur exportieren (alle fünf Stimmungen), mit Passwort
  await a.ev(`(()=>{const c=[...document.querySelectorAll('#avatar-gallery label.pick')].find(l=>l.innerText.includes('Passwort schützen')).querySelector('input');c.click()})()`);
  await until(a, `document.querySelectorAll('#avatar-gallery input[type=password]').length===2`);
  const PW = 'Korrekt-Pferd-Batterie-9';
  await a.type('#avatar-gallery .gallery-pw label:nth-child(1) input', PW); await a.type('#avatar-gallery .gallery-pw label:nth-child(2) input', PW); await sleep(300);
  await inScope(a, '#avatar-gallery', 'Figur exportieren');
  ok(await until(a, `document.body.innerText.includes('AES-256-geschützt')`, 15000), 'Export der Figur mit Passwort: Meldung');
  await sleep(800);
  const zip = readdirSync(dl).filter(x => x.endsWith('.zip')).map(x => join(dl, x))[0];
  if (SEVEN) {
    const t = spawnSync(SEVEN, ['t', zip, '-p' + PW], { encoding: 'utf8' });
    const l = spawnSync(SEVEN, ['l', zip, '-p' + PW], { encoding: 'utf8' }).stdout;
    ok(t.status === 0 && ['neutral', 'freundlich', 'motivierend', 'erfolgreich', 'ermutigend'].every(s => l.includes('-' + s + '.webp') || l.includes('-' + s + '.png')), '7-Zip öffnet das AES-Archiv; es enthält fünf Stimmungsdateien');
  } else ok(true, '7-Zip nicht vorhanden – Archivinhalt nicht mit einem unabhängigen Programm geprüft');

  // Reimport in ein zweites Konto
  const b = await newPage(`${B}/auth/login?devUser=${user}-import`); await sleep(2500);
  await b.go(`${B}/profile`); await live(b);
  const callsBeforeImport = (await stubLog()).length;
  const d = await b.send('DOM.getDocument'); const q = await b.send('DOM.querySelector', { nodeId: d.result.root.nodeId, selector: '#avatar-gallery input[type=file]' });
  await b.send('DOM.setFileInputFiles', { files: [zip], nodeId: q.result.nodeId });
  await b.waitText('Passwort des Archivs', 8000);
  await b.type('#avatar-gallery .gallery-pw input', PW); await sleep(300); await inScope(b, '#avatar-gallery', 'Archiv öffnen');
  ok(await until(b, `document.querySelectorAll('.gallery-import-list li').length===5`, 10000), 'Importvorschau zeigt alle fünf Bilder');
  await inScope(b, '#avatar-gallery', 'Import bestätigen');
  ok(await until(b, `document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing)').length===5`, 10000) && await count(b, '.gallery-figure-head') === 1 && await count(b, '.gallery-tile-missing') === 0, 'Reimport: eine vollständige Figur mit fünf Stimmungen');
  const labelsB = await b.ev(`[...document.querySelectorAll('.gallery-tile .badge-mood')].map(x=>x.innerText.trim()).sort().join(',')`);
  ok(labelsB === labels, 'Reimport: Stimmungszuordnung wiederhergestellt (' + labelsB + ')');
  const srcsB = await b.ev(`[...document.querySelectorAll('.gallery-tile img.avatar-img')].map(i=>i.getAttribute('src'))`);
  const hashesB = []; for (const s of srcsB) hashesB.push(await hashOf(b, s));
  ok([...hashesB].sort().join() === [...hashes].sort().join(), 'Reimport: Bilddateien unverändert (gleiche Prüfsummen)');
  ok((await stubLog()).length === callsBeforeImport, 'Der Reimport löst keine KI-Aufrufe aus');
  ok(await b.ev(`document.querySelector('.avatar-preview .avatar-initials')!==null`), 'Der Reimport ändert den Profilavatar nicht');

  // Hinweis bei fehlenden Stimmungen: bestehender Avatar nur mit Hauptbild
  const c = await newPage(`${B}/auth/login?devUser=${user}-only-main`); await sleep(2500);
  await c.go(`${B}/profile`); await live(c);
  await c.click('Textbeschreibung'); await c.type('.avatar-ai textarea', 'blonder Dutt');
  const t1 = Date.now(); let cl = false; while (!cl && Date.now() - t1 < 6000) { cl = await inScope(c, '.avatar-ai', 'Avatar erstellen'); if (!cl) await sleep(150); }
  await until(c, `document.querySelectorAll('.avatar-variant').length===1`);
  await c.click('Übernehmen'); await c.waitText('Profilavatar übernommen', 8000);
  await c.click('Freundlich'); await sleep(600);
  ok(await c.ev(`document.querySelector('.avatar-mood-hint')?.innerText.includes('kein eigenes Bild')`), 'Fehlende Stimmung: verständlicher Hinweis (Ersatz = Hauptvariante) statt stillschweigend ein anderes Bild');
  const callsHint = (await stubLog()).length;
  await c.click('Erfolgreich'); await sleep(600);
  ok((await stubLog()).length === callsHint, 'Das Anklicken einer fehlenden Stimmung löst keine kostenpflichtige Erzeugung aus');
  await c.go(`${B}/`); await sleep(1500);
  // nachträglich ergänzen für den bestehenden Avatar (mit Bestätigung)
  await c.go(`${B}/profile`); await live(c); await until(c, `document.querySelectorAll('.gallery-figure-head').length===1`);
  ok(await c.ev(`[...document.querySelectorAll('#avatar-gallery button')].some(b=>b.innerText.trim().startsWith('Alle 5 Stimmungen erstellen'))`), 'Bestehender KI-Avatar: „Alle 5 Stimmungen erstellen (4 Bilder)“ wird angeboten');
  await shot(c, 'moods-missing-desktop');
  await inScope(c, '#avatar-gallery', 'Erstellen (1 Bild)', 1);   // gezielt nur „Motivierend“ (zweiter Platzhalter)
  ok(await c.waitText('1 Bild(er)', 8000), 'Gezielt eine einzelne Stimmung erstellen: Bestätigung nennt 1 Bild');
  await inScope(c, '#avatar-gallery', 'Jetzt erzeugen');
  ok(await until(c, `document.querySelectorAll('.gallery-tile:not(.gallery-tile-missing)').length===2`, 30000) && await count(c, '.gallery-tile-missing') === 3, 'Nur diese eine Stimmung wurde ergänzt, drei bleiben fehlend (nichts automatisch)');

  // Mobil + Dark Mode
  await a.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'dark' }] });
  await a.send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 2, mobile: true });
  await a.go(`${A}/profile`); await live(a); await until(a, `document.querySelectorAll('.gallery-tile').length===5`);
  await a.ev(`document.querySelector('.gallery-figure-head')?.scrollIntoView()`); await sleep(500);
  ok(await a.ev(`document.documentElement.scrollWidth <= innerWidth + 1 && [...document.querySelectorAll('.gallery-tile, .gallery-figure-head')].every(t=>{const r=t.getBoundingClientRect();return r.left>=0&&r.right<=innerWidth+1})`), 'Mobil (390 px) und Dark Mode: Figur mit fünf Stimmungen ohne horizontales Scrollen');
  await shot(a, 'moods-gallery-mobile-dark');
  await a.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'light' }] });

  const errs = [a, b, c].flatMap(p => p.errors); ok(errs.length === 0, `Keine JavaScript-/Konsolenfehler (${errs.length}) ${errs.slice(0, 3).join(' | ')}`);
} catch (e) { console.log('SKRIPTFEHLER', e); await finish(1); }
try { rmSync(dl, { recursive: true, force: true }); } catch { /* egal */ }
await finish();
