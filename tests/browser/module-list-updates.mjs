// Modulliste: einklappbare Gruppen sowie Markierung „neuere Version“ mit Vergleichs-Overlay und ausdrücklicher Übernahme in den Themenentwurf (ADR 0030/0059).
// Voraussetzungen: siehe tests/browser/README.md (Dev-Instanz mit Wegwerf-Datenbank, AUTH_MODE=dev, Chrome, Node >= 22). Start: LEARNY_BROWSER_TEST=1 node tests/browser/module-list-updates.mjs
import { A, newPage, api, ok, sleep, J, finish } from './lib.mjs';

try {
  const a = await newPage(`${A}/auth/login?devUser=mu-owner`); await sleep(2500);
  const ts = Date.now();
  const name = 'MuMod-' + ts, topicName = 'MuThema ' + ts;
  const m = J(await api(a, '/api/modules/', 'POST', { name })).id;
  const c1 = J(await api(a, '/api/cards/', 'POST', { module: name, category: 'K', term: 'Karte Alt', question: 'Frage?', definition: 'Alter Text' })).id;
  await api(a, '/api/cards/', 'POST', { module: name, category: 'K', term: 'Karte Gleich', question: 'Frage?', definition: 'Bleibt' });
  const t = J(await api(a, '/api/topics/', 'POST', { name: topicName, description: '' })).id;
  await api(a, `/api/topics/${t}/modules`, 'POST', { module_id: m });   // Version 1 festgelegt
  await api(a, `/api/cards/${c1}`, 'PUT', { module: name, category: 'K', term: 'Karte Alt', question: 'Frage?', definition: 'Neuer Text' });
  await api(a, '/api/cards/', 'POST', { module: name, category: 'K', term: 'Karte Neu', question: 'Frage?', definition: 'Ganz neu' });
  const pub = await api(a, `/api/modules/${m}/versions`, 'POST', { note: 'Zwei' });
  ok(pub.s === 201, 'Aufbau: Modul in Thema (V1 festgelegt), danach V2 mit geänderter und neuer Karte veröffentlicht');

  const setGroup = () => a.ev(`(()=>{const s=[...document.querySelectorAll('select')].find(x=>[...x.options].some(o=>o.value==='topic'));if(!s)return false;s.value='topic';s.dispatchEvent(new Event('change',{bubbles:true}));return true})()`);

  // ─── Listenansicht, gruppiert nach Thema ───
  await a.go(`${A}/?view=list`); await a.waitText('MuMod-');
  ok(await setGroup(), 'Gruppierung „Thema“ gewählt'); await sleep(800);
  ok(await a.waitText(topicName) && await a.waitText(name), 'Gruppe mit dem Thema und das Modul sind sichtbar');
  ok(await a.waitText('mit neuerer Version') && await a.waitText('↑ V2 neu'), 'Gruppenkopf und Zeile markieren die neuere Version');
  const toggle = () => a.ev(`(()=>{const b=[...document.querySelectorAll('.group-toggle')].find(x=>x.innerText.includes(${JSON.stringify(topicName)}));if(!b)return false;b.click();return true})()`);
  const expanded = () => a.ev(`[...document.querySelectorAll('.group-toggle')].find(x=>x.innerText.includes(${JSON.stringify(topicName)}))?.getAttribute('aria-expanded')`);
  ok(await expanded() === 'true', 'Gruppe ist standardmäßig aufgeklappt (aria-expanded=true)');
  await toggle(); await sleep(500);
  ok(await expanded() === 'false' && !(await a.text()).includes(name + '\n') && !(await a.text()).includes('↑ V2 neu'), 'Zuklappen: Module der Gruppe verschwinden, aria-expanded=false');
  ok((await a.text()).includes('mit neuerer Version'), 'Gruppenkopf zeigt auch zugeklappt den Hinweis auf neuere Versionen');
  await a.click('Alle aufklappen'); await sleep(500);
  ok(await expanded() === 'true' && (await a.text()).includes('↑ V2 neu'), '„Alle aufklappen“ zeigt die Module wieder');
  await a.click('Alle zuklappen'); await sleep(400);
  ok(await expanded() === 'false', '„Alle zuklappen“ klappt die Gruppen zu');
  await a.click('Alle aufklappen'); await sleep(400);

  // ─── Overlay: Vergleich mit Markierung ───
  await a.ev(`document.querySelector('.update-badge').click()`);
  ok(await a.waitText('Änderungen: ' + name) && await a.waitText('festgelegt V1 → neu V2'), 'Overlay öffnet auf der Listenseite und nennt festgelegte und neue Version');
  const marks = await a.ev(`({added:[...document.querySelectorAll('.diff-added')].map(x=>x.innerText).join('|'),changed:[...document.querySelectorAll('.diff-changed')].map(x=>x.innerText).join('|'),old:[...document.querySelectorAll('.diff-old')].map(x=>x.innerText).join('|'),nw:[...document.querySelectorAll('.diff-new')].map(x=>x.innerText).join('|'),all:document.querySelector('.drawer-body').innerText})`);
  ok(marks.added.includes('Karte Neu') && marks.changed.includes('Karte Alt') && marks.old.includes('Alter Text') && marks.nw.includes('Neuer Text') && !marks.all.includes('Karte Gleich'), 'Änderungen sind markiert: neue Karte, geänderte Karte mit altem/neuem Text, unveränderte Karte fehlt');
  const pinBefore = (await api(a, `/api/topics/${t}`)).j;
  await a.click('In Thema übernehmen'); ok(await a.waitText('Ja, V2 in'), 'Übernahme verlangt eine Bestätigung');
  await a.click('Abbrechen', 'button'); await sleep(300);
  ok((await a.text()).includes('In Thema übernehmen') && (await a.text()).includes('↑ V2 neu'), 'Abbrechen: nichts übernommen, Markierung bleibt');
  await a.click('In Thema übernehmen'); await a.waitText('Ja, V2 in');
  await a.ev(`[...document.querySelectorAll('.drawer button')].find(x=>x.innerText.startsWith('Ja, V2')).click()`);
  ok(await a.waitText('wurde im Entwurf des Themas') && (await a.text()).includes('noch nicht veröffentlicht'), 'Übernahme: Meldung „im Entwurf übernommen (noch nicht veröffentlicht)“');
  const vers = (await api(a, `/api/topics/${t}/versions`)).j;
  ok(!vers.includes('"version"') || JSON.parse(vers).length === 0, 'Keine Themenversion veröffentlicht');
  await a.click('✕', 'button'); await sleep(500);
  ok(!(await a.text()).includes('↑ V2 neu') && !(await a.text()).includes('mit neuerer Version'), 'Nach der Übernahme verschwindet die Markierung in Liste und Gruppenkopf');

  // ─── Kachelansicht ───
  await api(a, `/api/cards/${c1}`, 'PUT', { module: name, category: 'K', term: 'Karte Alt', question: 'Frage?', definition: 'Noch neuer' });
  await api(a, `/api/modules/${m}/versions`, 'POST', { note: 'Drei' });
  await a.go(`${A}/?view=tiles`); await a.waitText('MuMod-'); await setGroup(); await sleep(800);
  ok(await a.waitText('↑ V3 neu'), 'Kachelansicht: Markierung für die nächste neuere Version (V3)');
  await toggle(); await sleep(400);
  ok(!(await a.text()).includes('↑ V3 neu') && await expanded() === 'false', 'Kachelansicht: Gruppe lässt sich zuklappen');

  // ─── Entwurf mit Änderungen (nicht veröffentlicht): markieren, vergleichen, veröffentlichen ───
  const name2 = 'MuDraft-' + ts;
  const m2 = J(await api(a, '/api/modules/', 'POST', { name: name2 })).id;
  const d1 = J(await api(a, '/api/cards/', 'POST', { module: name2, category: 'K', term: 'Entwurfskarte', question: 'Frage?', definition: 'Version eins' })).id;
  await api(a, `/api/modules/${m2}/versions`, 'POST', { note: 'Eins' });
  await api(a, `/api/cards/${d1}`, 'PUT', { module: name2, category: 'K', term: 'Entwurfskarte', question: 'Frage?', definition: 'Nur im Entwurf' });
  await a.go(`${A}/?view=list`); await a.waitText(name2);
  ok(await a.waitText('✎ Entwurf mit Änderungen'), 'Modul mit unveröffentlichten Änderungen ist in der Liste markiert');
  const rowText = () => a.ev(`[...document.querySelectorAll('tr')].find(r=>r.innerText.includes(${JSON.stringify(name2)}))?.innerText`);
  ok((await rowText()).includes('Entwurf mit Änderungen') && !(await rowText()).includes('↑ V'), 'Zeile des Moduls zeigt den Hinweis (und keine „neue Version“-Markierung)');
  await a.ev(`[...document.querySelectorAll('tr')].find(r=>r.innerText.includes(${JSON.stringify(name2)})).querySelector('.update-badge').click()`);
  ok(await a.waitText('Entwurf mit Änderungen gegenüber V1') && await a.waitText('Nur im Entwurf'), 'Overlay zeigt Vergleich Version ↔ Entwurf mit markierter Änderung');
  const dm = await a.ev(`({changed:[...document.querySelectorAll('.diff-changed')].map(x=>x.innerText).join('|'),old:[...document.querySelectorAll('.diff-old')].map(x=>x.innerText).join('|')})`);
  ok(dm.changed.includes('Entwurfskarte') && dm.old.includes('Version eins'), 'Geänderte Karte mit altem und neuem Text markiert');
  await a.click('⬆ Version veröffentlichen'); ok(await a.waitText('Ja, Version veröffentlichen'), 'Veröffentlichen verlangt eine Bestätigung');
  await a.click('Abbrechen', 'button'); await sleep(300);
  ok((await api(a, `/api/modules/${m2}/versions`)).j.includes('"latest_version":1'), 'Abbrechen: keine Version erzeugt');
  await a.click('⬆ Version veröffentlichen'); await a.waitText('Ja, Version veröffentlichen');
  await a.ev(`[...document.querySelectorAll('.drawer button')].find(x=>x.innerText.startsWith('Ja, Version')).click()`);
  ok(await a.waitText('Version 2 veröffentlicht.'), 'Veröffentlichen im Overlay: „Version 2 veröffentlicht.“');
  ok(J(await api(a, `/api/modules/${m2}/versions`)).latest_version === 2, 'Datenzustand: Modulversion 2 vorhanden');
  await a.click('✕', 'button'); await sleep(500);
  ok(!(await rowText()).includes('Entwurf mit Änderungen'), 'Nach dem Veröffentlichen verschwindet die Markierung');

  const errs = a.errors; ok(errs.length === 0, `Keine JavaScript-/Konsolenfehler (${errs.length}) ${errs.slice(0, 3).join(' | ')}`);
} catch (e) { console.log('SKRIPTFEHLER', e); await finish(1); }
await finish();
