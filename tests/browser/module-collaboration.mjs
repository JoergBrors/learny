// Modulzusammenarbeit mit zwei Sitzungen (ADR 0053): direkte Freigabe, Zugriff über Themen, Übersicht, Mehrfachzuordnung, Entzug, Mitarbeit beenden.
// Voraussetzungen: siehe tests/browser/README.md (Dev-Instanz mit Wegwerf-Datenbank, AUTH_MODE=dev, Chrome, Node >= 22). Start: LEARNY_BROWSER_TEST=1 node tests/browser/module-collaboration.mjs
import { A, B, newPage, api, ok, sleep, J, finish } from './lib.mjs';

try {
  const a = await newPage(`${A}/auth/login?devUser=mc-owner`); await sleep(2500);
  const b = await newPage(`${B}/auth/login?devUser=mc-co`); await sleep(2500);
  const meB = J(await api(b, '/api/me')); const ts = Date.now();
  const mDirect = J(await api(a, '/api/modules/', 'POST', { name: 'MDirekt-' + ts })).id;
  const mTopic = J(await api(a, '/api/modules/', 'POST', { name: 'MThema-' + ts })).id;
  const mLater = J(await api(a, '/api/modules/', 'POST', { name: 'MSpaeter-' + ts })).id;
  const mMulti = J(await api(a, '/api/modules/', 'POST', { name: 'MMehrfach-' + ts })).id;
  for (const [m, n] of [[mDirect, 'MDirekt-'], [mTopic, 'MThema-'], [mLater, 'MSpaeter-'], [mMulti, 'MMehrfach-']]) await api(a, '/api/cards/', 'POST', { module: n + ts, category: 'K', term: 'Karte ' + n, question: 'Q?', definition: 'D' });
  const t1 = J(await api(a, '/api/topics/', 'POST', { name: 'MC-T1 ' + ts, description: '' })).id, t2 = J(await api(a, '/api/topics/', 'POST', { name: 'MC-T2 ' + ts, description: '' })).id;
  for (const [t, m] of [[t1, mTopic], [t1, mMulti], [t2, mMulti]]) await api(a, `/api/topics/${t}/modules`, 'POST', { module_id: m });
  await api(a, `/api/topics/${t1}/coauthors`, 'POST', { user_id: meB.id });

  // 1. Eigentümer gibt Modul direkt frei (Oberfläche)
  await a.go(`${A}/module/${mDirect}/edit`);
  ok(await a.waitText('Zusammenarbeit') && await a.waitText('Direkte Co-Autoren'), 'Eigentümer: Modul-Editor zeigt „Zusammenarbeit“ mit direkten Co-Autoren');
  await a.click('Co-Author hinzufügen'); await sleep(500);
  await a.type('input[aria-label="Benutzer für die Modulzusammenarbeit suchen"]', 'mc-co'); await sleep(1500);
  ok(await a.click('Auswählen'), 'Eigentümer: Kandidat gefunden');
  ok(await a.waitText('darf die Karten, das Quiz'), 'Eigentümer: Bestätigungsdialog');
  await a.click('Als Co-Author hinzufügen'); ok(await a.waitText('ist jetzt direkter Co-Author'), 'Eigentümer: direkter Modul-Co-Author hinzugefügt');

  // 2. Übersicht „Gemeinsam bearbeitet“
  await b.go(`${B}/`); await b.waitText('Meine Module');
  ok(await b.click('Gemeinsam bearbeitet', '[role=tab]'), 'Co-Autor: Reiter „Gemeinsam bearbeitet“ vorhanden'); await sleep(800);
  const ov = await b.text();
  ok(ov.includes('MDirekt-') && ov.includes('Direkt als Co-Author') && ov.includes('MThema-') && ov.includes('Über Thema „MC-T1'), 'Übersicht: direkt freigegebenes Modul und Thema-Modul mit Zugriffsart');
  ok(!ov.includes('MMehrfach-') && !ov.includes('MSpaeter-'), 'Übersicht: Mehrfachzuordnung und noch nicht zugeordnetes Modul erscheinen nicht (keine fremde Bibliothek)');

  // 3. abgeleiteter Zugriff im Editor getrennt erkennbar, ohne Verwaltung
  await b.go(`${B}/module/${mTopic}/edit`);
  ok(await b.waitText('über ein gemeinsam bearbeitetes Thema') && !(await b.has('Co-Author hinzufügen')), 'Abgeleiteter Zugriff: Hinweis mit Thema, keine Verwaltung direkter Co-Autoren');
  ok(!(await b.text()).includes('Version veröffentlichen'), 'Abgeleiteter Zugriff: keine Modulveröffentlichung');
  await b.go(`${B}/module/${mDirect}/edit`);
  ok(await b.waitText('Direkte Co-Autoren') && await b.has('Co-Author hinzufügen') && await b.has('Zusammenarbeit beenden'), 'Direkter Zugriff: Verwaltung direkter Co-Autoren und Beenden vorhanden');

  // 4. Themeneditor: Links, später hinzugefügtes Modul, Mehrfachzuordnung
  await b.go(`${B}/topics/${t1}/edit`); await b.waitText('Zusammenarbeit');
  const te = async () => await b.ev(`[...document.querySelectorAll('li.topic-module-row')].map(li=>({t:li.innerText,l:!!li.querySelector('a[href*="/edit"]')}))`);
  let rows = await te();
  ok(rows.some(r => r.t.includes('MThema-') && r.l) && rows.some(r => r.t.includes('MMehrfach-') && !r.l && r.t.includes('nicht gemeinsam bearbeitbar')), 'Themeneditor: „Karten bearbeiten“ nur beim zugänglichen Modul, Mehrfachzuordnung als „nicht gemeinsam bearbeitbar“');
  await api(a, `/api/topics/${t1}/modules`, 'POST', { module_id: mLater });
  await b.go(`${B}/topics/${t1}/edit`); await b.waitText('Zusammenarbeit'); rows = await te();
  ok(rows.some(r => r.t.includes('MSpaeter-') && r.l), 'Nachträglich hinzugefügtes Modul ist ohne weitere Freigabe sofort bearbeitbar');
  await b.go(`${B}/module/${mLater}/edit`); ok(await b.waitText('Karte MSpaeter-') || await b.waitText('Karten'), 'Nachträgliches Modul öffnet im Editor');

  // 5. Entzug des Themenrechts: abgeleitetes Recht weg, direkte Freigabe bleibt
  await api(a, `/api/topics/${t1}/coauthors/${meB.id}`, 'DELETE');
  await b.go(`${B}/module/${mTopic}/edit`); ok(await b.waitText('Das Modul wurde nicht gefunden oder du darfst es nicht bearbeiten'), 'Themenentzug: abgeleitetes Modulrecht entfällt sofort');
  await b.go(`${B}/module/${mDirect}/edit`); ok(await b.waitText('Direkte Co-Autoren'), 'Themenentzug: direkte Modulfreigabe bleibt bestehen');

  // 6. Entzug der direkten Freigabe in offener Sitzung
  await b.click('Karte MDirekt-', 'button.editor-card-main'); await sleep(800);
  await b.type('input[maxlength="300"]', 'Nach Entzug direkt');
  await api(a, `/api/modules/${mDirect}/coauthors/${meB.id}`, 'DELETE');
  await b.click('Speichern'); await sleep(1500);
  const em = await b.ev(`document.querySelector('.alert-error')?.innerText ?? ''`); const st = (await api(a, '/api/modules/')).j;
  const dump = await api(a, `/api/modules/${mDirect}/export`);
  ok(em.includes('entzogen'), 'Direkter Entzug in offener Sitzung: Meldung sichtbar (' + em.replace(/\s+/g, ' ').slice(0, 80) + ')');
  ok(dump.j.includes('Karte MDirekt-') && !dump.j.includes('Nach Entzug direkt'), 'Direkter Entzug: Datenzustand unverändert');

  // 7. erneut freigeben, Mitarbeit beenden (Sicherheitsabfrage)
  await api(a, `/api/modules/${mDirect}/coauthors`, 'POST', { user_id: meB.id });
  await b.go(`${B}/module/${mDirect}/edit`); await b.waitText('Direkte Co-Autoren');
  await b.click('Zusammenarbeit beenden'); ok(await b.waitText('Du verlierst sofort die direkte Bearbeitung'), 'Sicherheitsabfrage beim Beenden');
  await b.click('Ja, beenden'); await sleep(2000);
  ok((await b.ev('location.pathname')) === '/' && (await api(b, `/api/modules/${mDirect}/coauthors`)).s === 404, 'Mitarbeit beendet: zurück auf der Modulseite, kein Zugriff mehr');

  const errs = [...a.errors, ...b.errors]; ok(errs.length === 0, `Keine JavaScript-/Konsolenfehler (${errs.length}) ${errs.slice(0, 3).join(' | ')}`);
} catch (e) { console.log('SKRIPTFEHLER', e); await finish(1); }
await finish();
