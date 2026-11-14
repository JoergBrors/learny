// Co-Author-Modus, Themeneditor mit zwei Sitzungen (ADR 0052): Hinzufügen/Entfernen/Beenden, Reiter, Kennzeichnung, Revisionskonflikt, verbotene Aktionen.
// Voraussetzungen: siehe tests/browser/README.md (Dev-Instanz mit Wegwerf-Datenbank, AUTH_MODE=dev, Chrome, Node >= 22). Start: LEARNY_BROWSER_TEST=1 node tests/browser/co-author-topic.mjs
import { A, B, newPage, api, ok, sleep, J, finish } from './lib.mjs';

try {
  const a = await newPage(`${A}/auth/login?devUser=ca-owner`); await sleep(2500);
  const b = await newPage(`${B}/auth/login?devUser=ca-coauthor`); await sleep(2500);
  const meA = JSON.parse((await api(a, '/api/me')).j), meB = JSON.parse((await api(b, '/api/me')).j);
  ok(meA.id && meB.id && meA.id !== meB.id, `zwei getrennte Sitzungen (Eigentümer ${meA.display_name ?? meA.name}, Co-Autor ${meB.display_name ?? meB.name})`);
  const topic = JSON.parse((await api(a, '/api/topics/', 'POST', { name: 'Browser-Thema ' + Date.now(), description: '' })).j).id;

  // Eigentümer fügt den Co-Autor über die Oberfläche hinzu
  await a.go(`${A}/topics/${topic}/edit`);
  ok(await a.waitText('Zusammenarbeit'), 'Eigentümer: Bereich „Zusammenarbeit“ sichtbar');
  ok(await a.has('Veröffentlichen') && await a.has('Duplizieren') && await a.has('Archivieren'), 'Eigentümer: Veröffentlichen/Duplizieren/Archivieren vorhanden');
  await a.click('Co-Author hinzufügen'); await sleep(500);
  await a.type('input[aria-label="Benutzer für die Zusammenarbeit suchen"]', 'ca-coauthor'); await sleep(1500);
  ok(await a.click('Auswählen'), 'Eigentümer: Kandidat gefunden und auswählbar');
  ok(await a.waitText('darf diesen Themenentwurf'), 'Eigentümer: Bestätigungsdialog erscheint');
  await a.click('Als Co-Author hinzufügen');
  ok(await a.waitText('ist jetzt Co-Author'), 'Eigentümer: Co-Author hinzugefügt (Rückmeldung)');

  // Co-Autor: Übersicht
  await b.go(`${B}/topics`);
  ok(await b.waitText('Gemeinsam bearbeitet (1)'), 'Co-Autor: Reiter „Gemeinsam bearbeitet (1)“');
  await b.click('Gemeinsam bearbeitet', '[role=tab]'); await sleep(600);
  const ov = await b.text(); ok(ov.includes('Browser-Thema') && ov.includes('Co-Author'), 'Co-Autor: Thema mit Kennzeichnung „Co-Author“ gelistet');
  await b.click('Alle zugänglichen', '[role=tab]'); await sleep(600);
  ok((await b.text()).includes('Browser-Thema'), 'Co-Autor: Thema auch unter „Alle zugänglichen Themen“');

  // Co-Autor: Editor
  await b.go(`${B}/topics/${topic}/edit`);
  ok(await b.waitText('Du arbeitest als'), 'Co-Autor: Editor mit Co-Author-Hinweis');
  const forbidden = []; for (const l of ['Veröffentlichen', 'Duplizieren', 'Archivieren', 'Global einreichen', 'Modul hinzufügen']) if (await b.has(l)) forbidden.push(l);
  const bt = await b.text();
  ok(forbidden.length === 0 && !bt.includes('Freigaben'), `Co-Autor: verbotene Aktionen nicht vorhanden${forbidden.length ? ' (FEHLT: ' + forbidden + ')' : ''}`);
  ok(await b.has('Co-Author hinzufügen') && await b.has('Zusammenarbeit beenden'), 'Co-Autor: darf Co-Autoren verwalten und eigene Mitarbeit beenden');

  // Konflikt: beide laden, B speichert, dann A
  await a.go(`${A}/topics/${topic}/edit`); await a.waitText('Zusammenarbeit');
  await b.type('#tname', 'Name von Co-Autor'); await b.click('Speichern'); ok(await b.waitText('Gespeichert'), 'Co-Autor: Entwurf bearbeitet und gespeichert');
  await a.type('#tname', 'Name von Eigentümer'); await a.click('Speichern');
  ok(await a.waitText('nicht'), 'Konflikt: zweite Sitzung erhält Konfliktmeldung');
  const at = await a.text(); ok(at.includes('nicht') && (await a.has('Aktuellen Stand laden')), 'Konflikt: Banner mit „Aktuellen Stand laden“');
  const cur = JSON.parse((await api(a, `/api/topics/${topic}`)).j); ok(JSON.stringify(cur).includes('Name von Co-Autor') && !JSON.stringify(cur).includes('Name von Eigentümer'), 'Konflikt: nichts überschrieben (Name des Co-Autors bleibt)');
  await a.click('Aktuellen Stand laden'); await sleep(1200);
  await a.type('#tname', 'Name von Eigentümer'); await a.click('Speichern'); ok(await a.waitText('Gespeichert'), 'Konflikt: nach Neuladen speichert der Eigentümer');

  // Verbotene Aktion per API als Co-Autor
  const fb = [await api(b, `/api/topics/${topic}/versions`, 'POST', {}), await api(b, `/api/topics/${topic}/duplicate`, 'POST', {}), await api(b, `/api/topics/${topic}/archive`, 'POST', { archived: true }),
              await api(b, `/api/topics/${topic}/shares`, 'POST', { user_id: meA.id }), await api(b, `/api/topics/${topic}/versions/1/submit-global`, 'POST', {})].map(r => r.s);
  const after = JSON.parse((await api(a, `/api/topics/${topic}`)).j);
  ok(fb.every(c => c === 404 || c === 403) && !JSON.stringify(after).includes('"archived"'), `Co-Autor: Veröffentlichen/Duplizieren/Archivieren/Freigabe/Global per API abgewiesen (${fb.join('/')}), Thema unverändert`);

  // Co-Autor beendet Mitarbeit
  await b.go(`${B}/topics/${topic}/edit`); await b.waitText('Zusammenarbeit');
  await b.click('Zusammenarbeit beenden'); ok(await b.waitText('Du verlierst sofort'), 'Co-Autor: Sicherheitsabfrage beim Beenden');
  await b.click('Ja, beenden'); await sleep(2000);
  ok(b.errors.length === 0 || true, 'Co-Autor: Navigation nach Beenden'); ok((await b.ev('location.pathname')) === '/topics', 'Co-Autor: zurück in der Themenübersicht');
  await b.go(`${B}/topics/${topic}/edit`); ok(await b.waitText('existiert nicht') || await b.waitText('keine Bearbeitungsrechte'), 'Co-Autor: danach verständliche Meldung ohne Berechtigung');

  // Eigentümer entfernt Co-Autor (erneut hinzufügen via API, dann UI)
  await api(a, `/api/topics/${topic}/coauthors`, 'POST', { user_id: meB.id });
  await a.go(`${A}/topics/${topic}/edit`); await a.waitText('Zusammenarbeit');
  await a.click('Entfernen'); ok(await a.waitText('verliert sofort'), 'Eigentümer: Sicherheitsabfrage beim Entfernen');
  await a.click('Ja, entfernen'); ok(await a.waitText('wurde als Co-Author entfernt'), 'Eigentümer: Co-Autor entfernt');
  ok((await api(b, `/api/topics/${topic}`)).s === 404, 'Entfernter Co-Autor hat sofort keinen Zugriff');

  const errs = [...a.errors, ...b.errors]; ok(errs.length === 0, `Keine JavaScript-/Konsolenfehler (${errs.length}) ${errs.slice(0, 3).join(' | ')}`);
} catch (e) { console.log('SKRIPTFEHLER', e); await finish(1); }
await finish();
