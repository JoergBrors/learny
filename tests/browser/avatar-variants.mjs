// Avatar-Varianten in der Profilseite (ADR 0051/0055/0056): mehrere Vorschläge sichtbar, einzeln auswählbar, Auswahl markiert, „Übernehmen“ speichert genau die Auswahl,
// „Verwerfen“/„Alle verwerfen“, Fehlerfall ohne Änderung, Neuladen zeigt nur den gespeicherten Avatar, Mobil/Dark Mode. Arbeitet im vorhandenen Modus:
//  - mit KI-Schlüssel (Bild-Stub, siehe avatar-image.mjs): KI-Bildavatar;  - ohne Schlüssel (OPENAI_API_KEY leer): einfacher SVG-Baukasten.
// Start: LEARNY_BROWSER_TEST=1 node tests/browser/avatar-variants.mjs  (Bild-Modus zusätzlich: node tests/browser/stub-image-api.mjs). Prüft nicht die Bildqualität eines echten Modells.
import { A, B, newPage, api, ok, sleep, finish } from './lib.mjs';
import { writeFileSync } from 'node:fs';

const STUB = process.env.STUB_URL ?? 'http://127.0.0.1:5298';
const SHOT = process.env.SHOT_DIR;
const shot = async (p, name) => { if (!SHOT) return; const r = await p.send('Page.captureScreenshot', { format: 'png' }); writeFileSync(`${SHOT}/${name}.png`, Buffer.from(r.result.data, 'base64')); };
const count = (p, sel) => p.ev(`document.querySelectorAll(${JSON.stringify(sel)}).length`);
const until = async (p, expr, ms = 9000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.ev(expr)) return true; await sleep(200); } return false; };
const MOOD = ['#F9A8D4', '#7F1D3D', '#FDE68A', '#F472B6', '#1F1B2E'];   // stimmungsabhängige Farben (Vorschau zeigt „Motivierend“, Kachel „Neutral“) ausblenden
const sig = (p, sel) => p.ev(`(()=>{const e=document.querySelector(${JSON.stringify(sel)});if(!e)return '';if(e.tagName==='IMG'){const u=e.src.replace(/&mood=\w+/,'');return u.slice(-80)+u.length}const mood=${JSON.stringify(MOOD)};return [...new Set([...e.querySelectorAll('[fill]')].map(x=>x.getAttribute('fill')).filter(f=>f&&f[0]==='#'&&!mood.includes(f.toUpperCase())))].sort().join(',')})()`);

try {
  const user = 'avvar-' + Date.now();
  const a = await newPage(`${A}/auth/login?devUser=${user}`); await sleep(2500);
  await a.go(`${A}/profile`);
  const imageMode = await a.waitText('KI-Bildavatar (echtes Bild)', 3000);
  console.log('Modus:', imageMode ? 'KI-Bildavatar (Stub)' : 'einfacher SVG-Baukasten');
  ok(await a.waitText('Mein Avatar'), 'Profil: Avatarbereich geladen');
  // Blazor Server: erst nach dem Verbindungsaufbau reagieren Schaltflächen; kalte Instanz → bis zu 15 s warten (Stimmungs-Chip dient als Prüfstein)
  const live = async p => { const t0 = Date.now(); while (Date.now() - t0 < 15000) { await p.click('Motivierend'); if (await p.ev(`document.querySelector('.avatar-preview .chip-active')?.innerText.trim()==='Motivierend'`)) return true; await sleep(300); } return false; };
  ok(await live(a), 'Profilseite ist interaktiv (Verbindung aufgebaut)');
  ok(await count(a, '.avatar-variant') === 0 && !(await a.text()).includes('Alle verwerfen'), 'Ohne Vorschläge: kein Variantenbereich (leerer Zustand)');

  const scope = imageMode ? '.avatar-ai' : '.avatar-gen:not(.avatar-ai)';
  const generate = async (text, label) => {
    if (imageMode) { await a.click('Textbeschreibung'); }
    await a.type(`${scope} textarea`, text);
    // erst klicken, wenn Blazor die Eingabe verarbeitet hat (Schaltfläche aktiv); sonst geht der Klick ins Leere
    const t0 = Date.now(); let clicked = false;
    while (!clicked && Date.now() - t0 < 6000) {
      clicked = await a.ev(`(()=>{const b=[...document.querySelectorAll(${JSON.stringify(scope + ' button')})].find(b=>/^(Avatar erstellen|Neue Variante erstellen|Vorschlag erstellen)/.test(b.innerText.trim())&&!b.disabled);if(!b)return false;b.click();return true})()`);
      if (!clicked) await sleep(150);
    }
  };
  // zwei deutlich verschiedene Vorschläge (im Bild-Modus zusätzlich anderer Stil)
  if (imageMode) await a.click('Klassischer Comic');
  await generate('graue kurze Haare, Vollbart, runde Brille');
  ok(await until(a, `document.querySelectorAll('.avatar-variant').length===1`), 'Nach der ersten Erzeugung: eine Variante in der Galerie');
  if (imageMode) await a.click('3D-Cartoon');
  await generate('Frau mit langen roten Haaren und gelbem Pullover');
  ok(await until(a, `document.querySelectorAll('.avatar-variant').length===2`), 'Nach der zweiten Erzeugung: beide Varianten sichtbar (die erste wurde nicht ersetzt)');
  const tiles = await a.ev(`[...document.querySelectorAll('.avatar-variant')].map(t=>t.getAttribute('aria-checked')+'|'+t.querySelector('.avatar-img, .avatar-svg')?.tagName)`);
  ok(tiles.length === 2 && tiles[0].startsWith('false') && tiles[1].startsWith('true'), 'Die zuletzt erzeugte Variante ist ausgewählt und markiert, die andere nicht (' + tiles.join(' / ') + ')');
  const sigs = [await sig(a, '.avatar-variant:nth-child(1) .avatar'), await sig(a, '.avatar-variant:nth-child(2) .avatar')];
  ok(sigs[0] !== sigs[1] && sigs[0] !== '' , 'Die Varianten sind unterscheidbar');
  ok(await a.ev(`(()=>{const g=document.querySelector('.avatar-variants');const r=g.getBoundingClientRect();return r.top>=0&&r.top<innerHeight})()`), 'Die Galerie liegt nach der Erzeugung im sichtbaren Bereich (automatisch gescrollt)');
  await shot(a, 'variants-desktop-light');

  // Auswahl wechseln
  await a.ev(`document.querySelector('.avatar-variant:nth-child(1)').click()`); await sleep(400);
  const sel2 = await a.ev(`[...document.querySelectorAll('.avatar-variant')].map(t=>t.getAttribute('aria-checked')).join(',')`);
  ok(sel2 === 'true,false', 'Auswahl wechseln: die angeklickte Variante ist markiert (' + sel2 + ')');
  const chosen = sigs[0];
  const chosenData = imageMode ? await a.ev(`document.querySelector('.avatar-variant:nth-child(1) img.avatar-img').src.split(',')[1]`) : null;

  // Fehlerfall (nur Bild-Modus, Stub): nichts ändert sich
  if (imageMode) {
    await fetch(STUB + '/__fail?n=1');
    await a.type(`${scope} textarea`, 'noch eine Variante'); await sleep(200);
    await a.click('Neue Variante erstellen');
    ok(await a.waitText('Dein bisheriger Avatar ist unverändert', 8000) && await count(a, '.avatar-variant') === 2, 'KI-Fehler: Meldung, die zwei Varianten bleiben, kein Geister-Vorschlag');
    ok((await api(a, '/api/me/avatar-image?nc=' + Date.now())).s === 404, 'KI-Fehler: gespeicherter Avatar unverändert (noch keiner gespeichert)');
  }

  // Übernehmen speichert genau die ausgewählte Variante
  await a.click('Übernehmen');
  ok(await a.waitText('Avatar gespeichert'), 'Übernehmen: Meldung „Avatar gespeichert“');
  ok(await until(a, `document.querySelectorAll('.avatar-variant').length===1`), 'Nach Übernehmen bleibt nur der übernommene Vorschlag weg (der andere bleibt als Vorschlag)');
  await a.go(`${A}/profile`); await a.waitText('Mein Avatar');
  await live(a);
  await until(a, `!!document.querySelector('.avatar-preview img.avatar-img, .avatar-preview svg.avatar-svg')`);
  const saved = await sig(a, '.avatar-preview .avatar');
  const savedData = imageMode ? await a.ev(`fetch('/api/me/avatar-image?nc='+Date.now()).then(r=>r.arrayBuffer()).then(b=>{let s='';const u=new Uint8Array(b);for(let i=0;i<u.length;i++)s+=String.fromCharCode(u[i]);return btoa(s)})`) : null;
  ok(saved !== '' && (imageMode ? savedData === chosenData && await a.ev(`!!document.querySelector('.avatar-preview img.avatar-img[src*="/api/me/avatar-image?v="]')`) : saved === chosen), 'Nach Neuladen: genau die ausgewählte Variante ist der gespeicherte Avatar und wird angezeigt');
  ok(await count(a, '.avatar-variant') === 0, 'Nach Neuladen: keine flüchtigen Vorschläge mehr');

  // Verwerfen
  await live(a);
  await generate('Mann mit Glatze und Bart');
  await until(a, `document.querySelectorAll('.avatar-variant').length===1`);
  await generate('Kind mit Stachelfrisur');
  await until(a, `document.querySelectorAll('.avatar-variant').length===2`);
  await a.click('Verwerfen');   // verwirft die ausgewählte Variante
  ok(await until(a, `document.querySelectorAll('.avatar-variant').length===1`), 'Verwerfen entfernt nur die ausgewählte Variante');
  await generate('Frau mit grauem Bob');
  await until(a, `document.querySelectorAll('.avatar-variant').length===2`);
  await a.click('Alle verwerfen');
  ok(await until(a, `document.querySelectorAll('.avatar-variant').length===0`) && await sig(a, '.avatar-preview .avatar') === saved, 'Alle verwerfen: Galerie leer, gespeicherter Avatar unverändert');

  // Fremder Benutzer
  const o = await newPage(`${B}/auth/login?devUser=${user}-other`); await sleep(2500); await o.go(`${B}/profile`); await o.waitText('Mein Avatar');
  ok(await count(o, '.avatar-variant') === 0 && (await api(o, '/api/me/avatar-image?nc=' + Date.now())).s === 404, 'Anderer Benutzer: sieht weder Varianten noch fremden Avatar');

  // Mobil + Dark Mode
  await generate('Person mit rotem Afro und Brille'); await until(a, `document.querySelectorAll('.avatar-variant').length>=1`);
  await generate('Person mit blondem Dutt'); await until(a, `document.querySelectorAll('.avatar-variant').length>=2`);
  await a.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'dark' }] });
  await a.send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 2, mobile: true });
  await sleep(600); await a.ev(`document.querySelector('.avatar-variants')?.scrollIntoView({block:'center'})`); await sleep(400);
  ok(await a.ev(`document.documentElement.scrollWidth <= innerWidth + 1 && [...document.querySelectorAll('.avatar-variant')].every(t=>{const r=t.getBoundingClientRect();return r.left>=0&&r.right<=innerWidth+1})`), 'Mobil (390 px) und Dark Mode: Galerie ohne horizontales Scrollen, alle Kacheln im Bild');
  ok(await a.ev(`(()=>{const t=document.querySelector('.avatar-variant');const r=t.getBoundingClientRect();return r.width>=100&&r.height>=100})()`), 'Kacheln haben eine sinnvolle Größe');
  await shot(a, 'variants-mobile-dark');

  const errs = [a, o].flatMap(p => p.errors); ok(errs.length === 0, `Keine JavaScript-/Konsolenfehler (${errs.length}) ${errs.slice(0, 3).join(' | ')}`);
} catch (e) { console.log('SKRIPTFEHLER', e); await finish(1); }
await finish();
