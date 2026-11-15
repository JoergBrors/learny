// KI-Bildavatar (ADR 0056) gegen den lokalen Bild-Stub: Text-/Foto-Erzeugung, Stile, mehrere Vorschläge, Übernehmen, Beständigkeit nach erneuter Anmeldung,
// Fehlerfall ohne Änderung des Avatars, Dark Mode und Mobilbreite (Screenshots optional über SHOT_DIR).
// Hinweis: Das Bild wird mit Cache-Control „immutable“ ausgeliefert (Version in der Adresse); direkte Prüfabrufe hängen deshalb einen Zufallsparameter an.
// Start (siehe tests/browser/README.md): node tests/browser/stub-image-api.mjs & Instanz mit OPENAI_API_KEY=stub-key OPENAI_BASE_URL=http://127.0.0.1:5298/v1,
// dann LEARNY_BROWSER_TEST=1 node tests/browser/avatar-image.mjs. Prüft NICHT die Bildqualität eines echten Modells.
import { A, B, newPage, api, ok, sleep, finish } from './lib.mjs';
import { writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const STUB = process.env.STUB_URL ?? 'http://127.0.0.1:5298';
const SHOT = process.env.SHOT_DIR;
const stub = async p => (await fetch(STUB + p)).text();
const shot = async (p, name) => { if (!SHOT) return; const r = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(`${SHOT}/${name}.png`, Buffer.from(r.result.data, 'base64')); };
// kleines gültiges PNG als „Foto“ (nur Bytes mit gültiger Struktur; der Stub wertet es nicht aus)
const photoB64 = Buffer.concat([Buffer.from([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13]), Buffer.from('IHDR'), Buffer.alloc(400, 7), Buffer.from('IEND'), Buffer.alloc(4)]).toString('base64');

try {
  const user = 'avimg-' + Date.now();
  const logBase = JSON.parse(await stub('/__log')).length;   // der Stub kann mehrere Läufe überdauern
  const a = await newPage(`${A}/auth/login?devUser=${user}`); await sleep(2500);
  await a.go(`${A}/profile`);
  ok(await a.waitText('KI-Bildavatar'), 'Profil: Bereich „KI-Bildavatar (echtes Bild)“ ist vorhanden');
  ok(await a.waitText('Einfacher Avatar-Baukasten (SVG, kein KI-Bild)'), 'Profil: SVG-Baukasten ist klar als einfacher Modus gekennzeichnet');
  ok((await a.text()).includes('externen KI-Dienst') && (await a.text()).includes('Aufbewahrungs'), 'Profil: Hinweis auf externe Verarbeitung und deren eigene Aufbewahrungsbedingungen');

  // Text → Bild, Stil klassisch
  await a.click('Textbeschreibung'); await a.click('Klassischer Comic');
  await sleep(300);
  ok(await a.ev(`!document.querySelector('.avatar-ai input[type=file]')`), 'Quelle „Textbeschreibung“: kein Foto-Upload');
  ok(await a.ev(`[...document.querySelectorAll('.avatar-ai button')].some(b=>b.innerText.startsWith('Avatar erstellen')&&b.disabled)`), 'Ohne Beschreibung ist „Avatar erstellen“ deaktiviert');
  await a.type('.avatar-ai textarea', 'graue kurze Haare, Vollbart, runde Brille');
  await sleep(300); await a.click('Avatar erstellen');
  ok(await a.waitText('Übernehmen', 8000) && await a.ev(`document.querySelectorAll('#avatar-variants img.avatar-img').length===1`), 'Text → ein Bildvorschlag erscheint');
  let log = JSON.parse(await stub('/__log')).slice(logBase);
  ok(log.length === 1 && log[0].path.endsWith('/images/generations') && log[0].model === 'gpt-image-2.5-flare' && !log[0].photo && log[0].style === 'classic' && log[0].wish.includes('Vollbart'), 'Anfrage: Generation-Endpunkt, Bildmodell, Stil „klassisch“, Wunsch im Auftrag, kein Foto');

  // zweite Variante in anderem Stil: erste bleibt erhalten
  await a.click('3D-Cartoon'); await sleep(200); await a.click('Neue Variante erstellen');
  ok(await a.ev(`new Promise(r=>{const t0=Date.now();const i=setInterval(()=>{if(document.querySelectorAll('#avatar-variants img.avatar-img').length===2||Date.now()-t0>8000){clearInterval(i);r(document.querySelectorAll('#avatar-variants img.avatar-img').length===2)}},200)})`), 'Neue Variante: zweiter Vorschlag, der erste bleibt erhalten');
  const srcs = await a.ev(`[...document.querySelectorAll('#avatar-variants img.avatar-img')].map(i=>i.src.slice(0,60)+i.src.length)`);
  ok(new Set(srcs).size === 2, 'Zwei Stile ergeben zwei verschiedene Bilder');
  await shot(a, 'desktop-light-proposals');

  // Fehlerfall: Avatar unverändert
  const before = await a.ev(`document.querySelector('header .avatar, .shell-user .avatar, .avatar')?.outerHTML?.length`);
  await stub('/__fail?n=1'); await a.click('Neue Variante erstellen');
  ok(await a.waitText('Dein bisheriger Avatar ist unverändert', 8000), 'KI-Fehler: verständliche Meldung');
  ok(await a.ev(`document.querySelectorAll('#avatar-variants img.avatar-img').length===2`) && (await api(a, '/api/me/avatar-image?nc=' + Date.now())).s === 404, 'KI-Fehler: Vorschläge bleiben, nichts wurde gespeichert');

  // Foto + Einwilligung
  await a.click('Foto und Text');
  const photoPath = join(tmpdir(), 'learny-avatar-test-photo.png'); writeFileSync(photoPath, Buffer.from(photoB64, 'base64'));
  const doc = await a.send('DOM.getDocument'); const q = await a.send('DOM.querySelector', { nodeId: doc.result.root.nodeId, selector: '.avatar-ai input[type=file]' });
  await a.send('DOM.setFileInputFiles', { files: [photoPath], nodeId: q.result.nodeId });
  const up = await a.waitText('Foto entfernen'); if (!up) console.log('DEBUG', await a.ev("document.querySelector('.avatar-ai').innerText.slice(-700)"));
  ok(up, 'Foto: Vorschau und „Foto entfernen“ erscheinen');
  ok(await a.ev(`[...document.querySelectorAll('.avatar-ai button')].some(b=>/Neue Variante|Avatar erstellen/.test(b.innerText)&&b.disabled)`), 'Foto ohne Einwilligung: Erzeugen ist gesperrt');
  await a.ev(`document.querySelector('.avatar-ai input[type=checkbox]').click()`); await sleep(300);
  await a.click('Verwerfen'); await sleep(300);   // ersten Vorschlag verwerfen, damit Platz ist
  await a.click('Neue Variante erstellen');
  await sleep(2500);
  log = JSON.parse(await stub('/__log')).slice(logBase);
  const last = log.at(-1);
  ok(last.path.endsWith('/images/edits') && last.model === 'gpt-image-2.5-sunburst' && last.photo && last.wish.includes('Vollbart'), 'Foto + Text: Edit-Endpunkt mit Foto, Bearbeitungsmodell und Wunsch');

  // Übernehmen und Beständigkeit
  await a.click('Übernehmen');
  ok(await a.waitText('KI-Avatar gespeichert'), 'Übernehmen: Meldung „KI-Avatar gespeichert“');
  const img = await api(a, '/api/me/avatar-image?nc=' + Date.now());
  ok(img.s === 200 && await a.ev(`!!document.querySelector('img.avatar-img[src^="/api/me/avatar-image?v="]')`), 'Übernommenes Bild wird als echtes Bild geliefert und im Profil angezeigt');
  ok(await a.ev(`document.querySelectorAll('#avatar-variants img.avatar-img').length===1`), 'Nach Übernehmen bleibt nur der andere Vorschlag übrig');
  await shot(a, 'desktop-light-accepted');
  const b = await newPage(`${A}/auth/login?devUser=${user}`); await sleep(2500);   // neue Anmeldung desselben Benutzers
  await b.go(`${A}/profile`);
  ok(await b.waitText('Mein Avatar') && await b.ev(`!!document.querySelector('img.avatar-img[src^="/api/me/avatar-image?v="]')`) && (await api(b, '/api/me/avatar-image?nc=' + Date.now())).s === 200, 'Nach erneuter Anmeldung: Bildavatar bleibt erhalten');

  // anderer Benutzer sieht nichts
  const o = await newPage(`${B}/auth/login?devUser=${user}-other`); await sleep(2500);
  ok((await api(o, '/api/me/avatar-image?nc=' + Date.now())).s === 404, 'Anderer Benutzer: kein Zugriff auf fremdes Avatarbild (404)');

  // SVG-Avatar wählen entfernt das Bild nur auf Benutzeraktion
  await a.click('Noah'); await a.waitText('Avatar gespeichert'); await sleep(500);
  ok((await api(a, '/api/me/avatar-image?nc=' + Date.now())).s === 404 && await a.ev(`!!document.querySelector('svg.avatar-svg')`), 'SVG-Avatar wählen: wird angezeigt (Bild entfernt auf Benutzeraktion)');

  // Darstellung: Dark Mode und Mobil
  await a.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'dark' }] });
  await a.send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 2, mobile: true });
  await a.go(`${A}/profile`); await a.ev(`document.querySelector('.avatar-ai')?.scrollIntoView()`); await sleep(500);
  ok(await a.ev(`document.documentElement.scrollWidth <= window.innerWidth + 1`), 'Mobil (390 px) und Dark Mode: kein horizontales Scrollen im Profil');
  await shot(a, 'mobile-dark-profile');

  try { rmSync(join(tmpdir(), 'learny-avatar-test-photo.png')); } catch { /* egal */ }
  const errs = [a, b, o].flatMap(p => p.errors); ok(errs.length === 0, `Keine JavaScript-/Konsolenfehler (${errs.length}) ${errs.slice(0, 3).join(' | ')}`);
} catch (e) { console.log('SKRIPTFEHLER', e); await finish(1); }
await finish();
