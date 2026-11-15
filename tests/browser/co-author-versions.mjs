// Co-Autor-Versionsverwaltung (ADR 0059) mit getrennten Sitzungen: Eigentümer (localhost), direkter Modul-Co-Autor (127.0.0.1), Themen-Co-Autor (::1).
// Voraussetzungen: siehe tests/browser/README.md (Dev-Instanz mit Wegwerf-Datenbank, AUTH_MODE=dev, Chrome, Node >= 22). Start: LEARNY_BROWSER_TEST=1 node tests/browser/co-author-versions.mjs
import { A, B, newPage, api, ok, sleep, J, finish } from './lib.mjs';

const C = process.env.LEARNY_URL_C ?? 'http://[::1]:5199';

try {
  const a = await newPage(`${A}/auth/login?devUser=cv-owner`); await sleep(2500);
  const b = await newPage(`${B}/auth/login?devUser=cv-direct`); await sleep(2500);
  const c = await newPage(`${C}/auth/login?devUser=cv-topic`); await sleep(2500);
  const meB = J(await api(b, '/api/me')), meC = J(await api(c, '/api/me'));
  const ts = Date.now();
  const mk = async (name, term) => {
    const id = J(await api(a, '/api/modules/', 'POST', { name })).id;
    const card = J(await api(a, '/api/cards/', 'POST', { module: name, category: 'Kat', term, question: 'Frage?', definition: 'Def' })).id;
    return { id, card, name };
  };
  const dm = await mk('VDirekt-' + ts, 'Direktkarte');
  const tm = await mk('VThema-' + ts, 'Themakarte');
  const topic = J(await api(a, '/api/topics/', 'POST', { name: 'VThema ' + ts, description: '' })).id;
  await api(a, `/api/topics/${topic}/modules`, 'POST', { module_id: tm.id });   // legt Modulversion 1 an und bindet sie
  await api(a, `/api/modules/${dm.id}/coauthors`, 'POST', { user_id: meB.id });
  await api(a, `/api/topics/${topic}/coauthors`, 'POST', { user_id: meC.id });
  const versions = async id => J(await api(a, `/api/modules/${id}/versions`));
  await api(a, `/api/modules/${dm.id}/versions`, 'POST', { note: 'Start' });
  ok(!!(dm.id && tm.id && topic) && (await versions(dm.id)).latest_version === 1 && (await versions(tm.id)).latest_version === 1, 'Aufbau: Modul mit direktem Co-Autor (V1), Modul im Thema mit Themen-Co-Autor (V1)');

  // ─── Direkter Co-Autor ───
  await b.go(`${B}/module/${dm.id}/edit`);
  ok(await b.waitText('Direktkarte') && await b.waitText('Version 1'), 'Direkter Co-Autor: Editor zeigt Version 1');
  const btnState = () => b.ev(`(()=>{const e=[...document.querySelectorAll('.module-versions button')].find(x=>x.innerText.includes('Version veröffentlichen'));return e?(e.disabled?'disabled':'enabled'):'missing'})()`);
  ok(await btnState() === 'disabled', 'Ohne Änderungen: „Version veröffentlichen“ sichtbar, aber gesperrt');
  ok(!(await b.text()).includes('veröffentlicht der Eigentümer'), 'Alter Hinweis „veröffentlicht der Eigentümer“ ist entfernt');

  await b.click('Direktkarte', 'button.editor-card-main'); await sleep(900);
  await b.type('input[maxlength="300"]', 'Direktkarte (Co)'); await sleep(500);
  ok(await btnState() === 'disabled' && await b.waitText('Speichere zuerst'), 'Ungespeicherte Karte: Veröffentlichen gesperrt, Hinweis „Speichere zuerst“');
  await b.click('Speichern'); ok(await b.waitText('Karte gespeichert'), 'Karte gespeichert');
  await b.ev(`window.confirm = () => true`);
  await sleep(500);
  await sleep(800);   // ohne Neuladen der Seite (Regression: Veröffentlichen muss direkt nach dem Speichern möglich sein)
  ok(await b.waitText('Entwurf mit Änderungen') && await btnState() === 'enabled', 'Nach Speichern: „Entwurf mit Änderungen“, Veröffentlichen möglich');

  await b.click('⬆ Version veröffentlichen');
  const confirmText = 'Eine neue unveränderliche Modulversion wird erstellt. Bestehende Themen und Lernfreigaben werden dadurch nicht automatisch geändert.';
  ok(await b.waitText(confirmText), 'Bestätigungstext erscheint wörtlich');
  await b.click('Abbrechen'); await sleep(400);
  ok(!(await b.text()).includes(confirmText) && (await versions(dm.id)).latest_version === 1, 'Abbrechen: kein Dialog, keine neue Version');
  await b.click('⬆ Version veröffentlichen'); await b.waitText(confirmText);
  await b.ev(`document.querySelector('[role=alertdialog] button.btn-primary').click()`);
  ok(await b.waitText('Version 2 veröffentlicht.'), 'Co-Autor veröffentlicht: Meldung „Version 2 veröffentlicht.“');
  await sleep(500);
  const t1 = await b.text();
  ok(t1.includes('Version 2') && !t1.includes('Entwurf mit Änderungen') && await btnState() === 'disabled', 'Nach Veröffentlichung: Status „Version 2“, kein Entwurf mit Änderungen, Knopf wieder gesperrt');
  ok(await b.ev(`[...document.querySelectorAll('.module-versions a')].map(x=>x.innerText.trim()).join(',')`) === 'V2,V1', 'Versionshistorie zeigt V2 und V1');
  const vs = await versions(dm.id);
  ok(vs.latest_version === 2 && vs.versions.length === 2, 'Datenzustand: genau zwei Versionen');

  // ─── Versionsseite (schreibgeschützt) ───
  await b.go(`${B}/module/${dm.id}/version/1`);
  ok(await b.waitText('Modulversion 1') && await b.waitText('nur lesbar') && await b.waitText('Direktkarte'), 'Version 1 lässt sich öffnen und zeigt den alten Kartenstand');
  ok(!(await b.ev(`[...document.querySelectorAll('.module-page ol li')].map(x=>x.innerText).join('|')`)).includes('Direktkarte (Co)'), 'Kartenliste von Version 1 enthält nicht den neuen Titel (nur der Vergleich nennt ihn)');
  const ctrls = await b.ev(`document.querySelectorAll('.module-page input, .module-page textarea, .module-page select, .module-page button').length`);
  ok(ctrls === 0, `Versionsseite ist schreibgeschützt (keine Eingabe-/Schaltflächen, gefunden: ${ctrls})`);
  await b.go(`${B}/module/${dm.id}/version/2`);
  ok(await b.waitText('Modulversion 2') && await b.waitText('Direktkarte (Co)') && await b.waitText('Keine Unterschiede'), 'Version 2 zeigt den neuen Stand; Vergleich mit dem Entwurf: keine Unterschiede');

  // ─── Automatische Aktualisierung (Änderung durch den Eigentümer) ───
  await b.go(`${B}/module/${dm.id}/edit`); await b.waitText('Direktkarte (Co)');
  await api(a, `/api/cards/${dm.card}`, 'PUT', { module: dm.name, category: 'Kat', term: 'Direktkarte (Auto)', question: 'Frage?', definition: 'Def' });
  ok(await b.waitText('Direktkarte (Auto)', 20000), 'Ohne ungespeicherte Eingaben: Änderung des Eigentümers erscheint automatisch in der Kartenliste');
  await b.click('Direktkarte (Auto)', 'button.editor-card-main'); await sleep(900);
  await b.type('input[maxlength="300"]', 'Lokal getippt'); await sleep(400);
  await api(a, `/api/cards/${dm.card}`, 'PUT', { module: dm.name, category: 'Kat', term: 'Direktkarte (Auto2)', question: 'Frage?', definition: 'Def' });
  ok(await b.waitText('inzwischen von jemand anderem geändert', 20000), 'Mit ungespeicherten Eingaben: Hinweis statt stillem Überschreiben');
  ok(await b.ev(`document.querySelector('input[maxlength="300"]').value`) === 'Lokal getippt', 'Ungespeicherte Eingabe bleibt erhalten');
  await b.click('Aktualisieren');
  await sleep(1500);
  ok(await b.ev(`document.querySelector('input[maxlength="300"]')?.value`) === 'Direktkarte (Auto2)' && !(await b.text()).includes('inzwischen von jemand anderem geändert'), 'Aktualisieren übernimmt den neuen Stand und schließt den Hinweis');

  // ─── Konflikt: veralteter Editor ───
  await api(a, `/api/cards/${dm.card}`, 'PUT', { module: dm.name, category: 'Kat', term: 'Direktkarte (Owner1)', question: 'Frage?', definition: 'Def' });
  await b.go(`${B}/module/${dm.id}/edit`); await b.waitText('Direktkarte (Owner1)');   // Editor lädt den Stand mit Fingerabdruck H1
  await api(a, `/api/cards/${dm.card}`, 'PUT', { module: dm.name, category: 'Kat', term: 'Direktkarte (Owner2)', question: 'Frage?', definition: 'Def' });   // Eigentümer ändert danach (H2)
  await b.click('⬆ Version veröffentlichen'); await b.waitText(confirmText);
  await b.ev(`document.querySelector('[role=alertdialog] button.btn-primary').click()`); await sleep(1200);
  ok((await b.text()).includes('inzwischen geändert') && (await versions(dm.id)).latest_version === 2, 'Veralteter Editorstand: Meldung „inzwischen geändert“, keine Version veröffentlicht');
  await b.go(`${B}/module/${dm.id}/edit`); await b.waitText('Direktkarte (Owner2)');
  ok(await btnState() === 'enabled', 'Nach Neuladen: aktueller Stand ist veröffentlichbar');

  // ─── Entzug zwischen Bestätigungsdialog und Veröffentlichung ───
  await b.click('⬆ Version veröffentlichen'); await b.waitText(confirmText);
  await api(a, `/api/modules/${dm.id}/coauthors/${meB.id}`, 'DELETE');   // Entzug zwischen Bestätigungsdialog und Veröffentlichung
  await b.ev(`document.querySelector('[role=alertdialog] button.btn-primary').click()`);
  await sleep(1500);
  const t2 = await b.text();
  ok(t2.includes('Co-Author-Zugriff') && t2.includes('entzogen') && (await versions(dm.id)).latest_version === 2, 'Entzug vor dem Veröffentlichen: verständliche Meldung, keine Version erzeugt');
  await b.go(`${B}/module/${dm.id}/version/2`);
  ok(await b.waitText('nicht gefunden') && !(await b.text()).includes('Direktkarte'), 'Entzogener Co-Autor: Versionsseite nicht mehr zugänglich');
  await b.go(`${B}/module/${dm.id}/edit`);
  ok(await b.waitText('nicht gefunden') && !(await b.text()).includes('Direktkarte'), 'Entzogener Co-Autor: Editor nicht mehr zugänglich');
  ok((await api(b, `/api/modules/${dm.id}/versions`)).s === 404 && (await api(b, `/api/modules/${dm.id}/versions`, 'POST', {})).s === 404, 'REST bleibt eigentümergebunden (404 für Co-Autor)');

  // ─── Themen-Co-Autor ───
  await c.go(`${C}/module/${tm.id}/edit`);
  ok(await c.waitText('Themakarte') && await c.waitText('Version 1'), 'Themen-Co-Autor: Editor des Themenmoduls zeigt Version 1');
  await c.click('Themakarte', 'button.editor-card-main'); await sleep(900);
  await c.type('input[maxlength="300"]', 'Themakarte (Co)'); await c.click('Speichern'); ok(await c.waitText('Karte gespeichert'), 'Themen-Co-Autor: Karte gespeichert');
  await c.go(`${C}/module/${tm.id}/edit`); await c.waitText('Themakarte (Co)');
  await c.click('⬆ Version veröffentlichen'); await c.waitText(confirmText);
  await c.ev(`document.querySelector('[role=alertdialog] button.btn-primary').click()`);
  ok(await c.waitText('Version 2 veröffentlicht.'), 'Themen-Co-Autor veröffentlicht Version 2');
  ok((await versions(tm.id)).latest_version === 2, 'Datenzustand: Version 2 des Themenmoduls');

  // Thema bleibt auf Version 1; neue Version wird angezeigt; keine Themenversion entstanden
  await a.go(`${A}/topics/${topic}/edit`);
  ok(await a.waitText('Neuere Version 2 verfügbar'), 'Themeneditor des Eigentümers zeigt „Neuere Version 2 verfügbar“ (keine automatische Übernahme)');
  const tv = J(await api(a, `/api/topics/${topic}/versions`));
  ok(Array.isArray(tv) ? tv.length === 0 : (tv.versions ?? []).length === 0, 'Keine Themenversion durch die Modulveröffentlichung entstanden');
  ok((await api(c, `/api/topics/${topic}/publish`, 'POST', {})).s >= 400, 'Themen-Co-Autor kann das Thema weiterhin nicht veröffentlichen');

  const errs = [a, b, c].flatMap(p => p.errors); ok(errs.length === 0, `Keine JavaScript-/Konsolenfehler (${errs.length}) ${errs.slice(0, 3).join(' | ')}`);
} catch (e) { console.log('SKRIPTFEHLER', e); await finish(1); }
await finish();
