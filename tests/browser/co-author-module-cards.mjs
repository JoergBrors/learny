// Co-Author-Modus, Modul-/Kartenbearbeitung mit zwei Sitzungen (ADR 0052/0053): Karten ändern/anlegen, Eigentümergrenzen, Mehrfachzuordnung, updated_at-Konflikt, Quiz-Einstellungen, Entzug in offener Sitzung.
// Voraussetzungen: siehe tests/browser/README.md (Dev-Instanz mit Wegwerf-Datenbank, AUTH_MODE=dev, Chrome, Node >= 22). Start: LEARNY_BROWSER_TEST=1 node tests/browser/co-author-module-cards.mjs
import { A, B, newPage, api, ok, sleep, J, finish } from './lib.mjs';

try {
  const a = await newPage(`${A}/auth/login?devUser=ca-owner2`); await sleep(2500);
  const b = await newPage(`${B}/auth/login?devUser=ca-co2`); await sleep(2500);
  const meA = J(await api(a, '/api/me')), meB = J(await api(b, '/api/me'));
  const ts = Date.now();
  const m1 = J(await api(a, '/api/modules/', 'POST', { name: 'Mod1-' + ts })).id, m2 = J(await api(a, '/api/modules/', 'POST', { name: 'Mod2-' + ts })).id;
  const c1 = J(await api(a, '/api/cards/', 'POST', { module: 'Mod1-' + ts, category: 'Kat', term: 'Karte Eins', question: 'Frage?', definition: 'Def' })).id;
  await api(a, '/api/cards/', 'POST', { module: 'Mod2-' + ts, category: 'Kat', term: 'Geteilte Karte', question: 'Frage?', definition: 'Def' });
  const t1 = J(await api(a, '/api/topics/', 'POST', { name: 'T1 ' + ts, description: '' })).id, t2 = J(await api(a, '/api/topics/', 'POST', { name: 'T2 ' + ts, description: '' })).id;
  for (const [t, m] of [[t1, m1], [t1, m2], [t2, m2]]) await api(a, `/api/topics/${t}/modules`, 'POST', { module_id: m });
  await api(a, `/api/topics/${t1}/coauthors`, 'POST', { user_id: meB.id });
  ok(!!(m1 && m2 && t1 && t2 && c1), 'Aufbau: Modul 1 nur in Thema 1; Modul 2 in Thema 1 und Thema 2 (Co-Autor nur an Thema 1)');

  // 1. Co-Autor öffnet Modul 1 und bearbeitet eine Karte
  await b.go(`${B}/module/${m1}/edit`);
  ok(await b.waitText('Karte Eins'), 'Co-Autor: Modul des gemeinsamen Themas öffnet im Editor (Karte sichtbar)');
  ok(!(await b.text()).includes('Das Modul wurde nicht gefunden'), 'Co-Autor: keine Fehlermeldung beim Öffnen');
  await b.click('Karte Eins', 'button.editor-card-main'); await sleep(900);
  await b.type('input[maxlength="300"]', 'Karte Eins (Co)'); await sleep(300);
  await b.click('Speichern'); ok(await b.waitText('Karte gespeichert'), 'Co-Autor: vorhandene Karte bearbeitet und gespeichert');

  // 2. Eigentümer sieht die Änderung
  await a.go(`${A}/module/${m1}/edit`);
  ok(await a.waitText('Karte Eins (Co)'), 'Eigentümer sieht die Änderung des Co-Autors');

  // 3. Co-Autor legt neue Karte an
  await b.click('+ Neue Karte'); await sleep(900);
  await b.type('input[list="editor-categories"]', 'Kat'); await b.type('input[maxlength="300"]', 'Neue Karte vom Co');
  await b.type('textarea', 'Frage vom Co?');
  await b.ev(`document.querySelector('.rte-surface').focus()`); await b.send('Input.insertText', { text: 'Antwort vom Co' }); await b.ev(`document.querySelector('.rte-surface').blur()`); await sleep(500);
  await b.click('Speichern'); await sleep(1500);
  const saved2 = (await b.ev(`[...document.querySelectorAll('button.editor-card-main')].some(x=>x.innerText.includes('Neue Karte vom Co'))`));
  ok(saved2, 'Co-Autor: neue Karte angelegt und in der Liste (laut ADR erlaubt: Inhalte der zugeordneten Module)' + (saved2 ? '' : ' — Fehler: ' + (await b.ev(`document.querySelector('.alert-error')?.innerText`))));
  await a.go(`${A}/module/${m1}/edit`); ok(await a.waitText('Neue Karte vom Co'), 'Eigentümer sieht die neue Karte');

  // 4. Eigentümer bleibt Moduleigentümer; keine fremde Bibliothek
  const own = J(await api(a, '/api/modules/')); const ownIds = JSON.stringify(own);
  const coList = (await api(b, '/api/modules/')).j;
  ok(ownIds.includes(m1) && !coList.includes(m1) && !coList.includes(m2), 'Eigentümer behält das Modul; Co-Autor erhält keine fremde Modulbibliothek (Modulliste ohne Modul 1/2)');
  ok((await api(b, `/api/modules/${m1}/export`)).s === 404 && (await api(b, `/api/cards/${c1}`)).s === 404, 'Karten-/Modul-REST bleibt für den Co-Autor eigentümergebunden (404)');

  // 5. Modul in zusätzlichem, nicht zugänglichem Thema: keine gemeinsame Bearbeitung
  await b.go(`${B}/module/${m2}/edit`);
  ok(await b.waitText('Das Modul wurde nicht gefunden oder du darfst es nicht bearbeiten'), 'Modul 2 (zusätzlich in nicht zugänglichem Thema 2): keine Bearbeitung durch den Co-Autor');
  ok(!(await b.text()).includes('Geteilte Karte'), 'Modul 2: Karten bleiben unsichtbar');

  // 6. Konflikt (updated_at) mit zwei Sitzungen
  await a.go(`${A}/module/${m1}/edit`); await a.waitText('Karte Eins (Co)'); await a.click('Karte Eins (Co)', 'button.editor-card-main'); await sleep(900);
  await b.go(`${B}/module/${m1}/edit`); await b.waitText('Karte Eins (Co)'); await b.click('Karte Eins (Co)', 'button.editor-card-main'); await sleep(900);
  await b.type('input[maxlength="300"]', 'Karte Eins (Co2)'); await b.click('Speichern'); ok(await b.waitText('Karte gespeichert'), 'Konflikt: erste Sitzung speichert');
  await a.type('input[maxlength="300"]', 'Karte Eins (Owner)'); await a.click('Speichern');
  ok(await a.waitText('Neu laden (eigene Änderungen verwerfen)'), 'Konflikt: zweite Sitzung erhält Konfliktmeldung mit „Neu laden“ (updated_at-Schutz)');
  const dump = (await api(a, `/api/cards/${c1}`)).j; ok(dump.includes('Karte Eins (Co2)') && !dump.includes('(Owner)'), 'Konflikt: nichts überschrieben');

  // Quiz-Einstellungen im Editor (Co-Autor): Konfiguration lesen und speichern über den Bearbeitungskontext (ADR 0052/0053)
  const panelSave = async (page, count) => {
    if (!(await page.ev(`!!document.querySelector('input[type="number"][max="20"]')`))) { await page.click('⚙ Quiz-Einstellungen'); await sleep(900); }   // Umschalter nur öffnen, wenn das Panel zu ist
    await page.type('input[type="number"][max="20"]', String(count));
    return await page.ev(`(()=>{const i=document.querySelector('input[type="number"][max="20"]');let e=i;while(e&&!([...e.querySelectorAll('button')].some(b=>b.innerText.trim()==='Speichern')))e=e.parentElement;const b=e&&[...e.querySelectorAll('button')].find(x=>x.innerText.trim()==='Speichern');if(!b)return false;b.click();return true})()`);
  };
  await b.go(`${B}/module/${m1}/edit`); await b.waitText('Karte Eins');
  ok(await panelSave(b, 7) && await b.waitText('Einstellungen gespeichert'), 'Co-Autor: Quiz-Einstellungen speichern (Panel nutzt den Bearbeitungskontext)');
  ok(/question_?count"?\s*:\s*7/i.test((await api(a, `/api/modules/${m1}/quiz-config`)).j), 'Quiz-Einstellungen: Wert beim Eigentümer sichtbar');

  // 7. Entzug wirkt sofort (offene Sitzung des Co-Autors)
  await b.click('Karte Eins (Co2)', 'button.editor-card-main'); await sleep(600); await b.type('input[maxlength="300"]', 'Nach Entzug');
  await api(a, `/api/topics/${t1}/coauthors/${meB.id}`, 'DELETE');
  await b.click('Speichern'); await sleep(1500);
  const errMsg = await b.ev(`document.querySelector('.alert-error')?.innerText ?? ''`);
  const stateAfter = (await api(a, `/api/cards/${c1}`)).j;
  ok(errMsg.includes('entzogen'), 'Entzug: sichtbare Meldung „Co-Author-Zugriff … entzogen“ in der noch offenen Sitzung (' + errMsg.replace(/\s+/g, ' ').slice(0, 90) + ')');
  ok(stateAfter.includes('Karte Eins (Co2)') && !stateAfter.includes('Nach Entzug'), 'Entzug: Datenzustand unverändert (Karte weiterhin „Karte Eins (Co2)“)');
  // Quiz-Einstellungen nach dem Entzug: Speichern wird abgewiesen, Konfiguration bleibt bei 7
  const okPanel = await panelSave(b, 9); await sleep(1500);
  const panelErr = await b.ev(`document.querySelector('.alert-error')?.innerText ?? ''`);
  ok(okPanel && panelErr.includes('entzogen') && /question_?count"?\s*:\s*7/i.test((await api(a, `/api/modules/${m1}/quiz-config`)).j), 'Entzug: Quiz-Einstellungen werden abgewiesen (Meldung „entzogen“), Konfiguration unverändert (' + panelErr.replace(/\s+/g, ' ').slice(0, 70) + ')');
  // Lesen nach dem Entzug: weitere Karte öffnen wird abgewiesen, ihr Inhalt wird nicht geladen
  await b.ev(`document.querySelector('.alert-error')?.remove()`);
  await b.ev('window.confirm = () => true');   // ungespeicherte Änderung: den Verwerfen-Dialog (blockiert Headless-Chrome) automatisch bestätigen
  await b.click('Neue Karte vom Co', 'button.editor-card-main'); await sleep(1500);
  const readErr = await b.ev(`document.querySelector('.alert-error')?.innerText ?? ''`);
  const termNow = await b.ev(`document.querySelector('input[maxlength="300"]')?.value ?? ''`);
  ok(readErr.includes('entzogen') && termNow !== 'Neue Karte vom Co', 'Entzug: Karteninhalt wird in der offenen Sitzung nicht mehr geladen (Meldung „entzogen“, Feld zeigt „' + termNow + '“)');
  await b.go(`${B}/module/${m1}/edit`); ok(await b.waitText('Das Modul wurde nicht gefunden oder du darfst es nicht bearbeiten'), 'Entzug: Modul sofort nicht mehr bearbeitbar');

  const errs = [...a.errors, ...b.errors]; ok(errs.length === 0, `Keine JavaScript-/Konsolenfehler (${errs.length}) ${errs.slice(0, 3).join(' | ')}`);
} catch (e) { console.log('SKRIPTFEHLER', e); await finish(1); }
await finish();
