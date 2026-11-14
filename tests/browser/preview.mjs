// Lern- und Prüfungsvorschau / Testmodus (ADR 0054) mit getrennten Sitzungen: Eigentümer (localhost), Co-Autor (127.0.0.1), direkter Modul-Co-Autor bzw. Fremder (::1).
// Voraussetzungen: siehe tests/browser/README.md. Start: LEARNY_BROWSER_TEST=1 node tests/browser/preview.mjs
import { A, B, newPage, api, ok, sleep, J, finish } from './lib.mjs';

const C = process.env.LEARNY_URL_C ?? 'http://[::1]:5199';

try {
  const a = await newPage(`${A}/auth/login?devUser=pv-owner`); await sleep(2500);
  const b = await newPage(`${B}/auth/login?devUser=pv-co`); await sleep(2500);
  const c = await newPage(`${C}/auth/login?devUser=pv-direct`); await sleep(2500);
  const meB = J(await api(b, '/api/me')), meC = J(await api(c, '/api/me'));
  const ts = Date.now();
  const card = { category: 'K', question: 'Frage?', definition: 'Definition', quiz: [{ type: 'single_choice', question: 'Welche Antwort stimmt?', options: ['Richtig', 'B', 'C', 'D'], correct_index: 0, explanation: 'Weil A.' }] };
  const m1 = J(await api(a, '/api/modules/', 'POST', { name: 'PvMod-' + ts })).id;
  const m2 = J(await api(a, '/api/modules/', 'POST', { name: 'PvDirekt-' + ts })).id;
  await api(a, '/api/cards/', 'POST', { ...card, module: 'PvMod-' + ts, term: 'Vorschaukarte' });
  await api(a, '/api/cards/', 'POST', { ...card, module: 'PvDirekt-' + ts, term: 'Direktkarte' });
  const t = J(await api(a, '/api/topics/', 'POST', { name: 'PvThema ' + ts, description: '' })).id;
  await api(a, `/api/topics/${t}/modules`, 'POST', { module_id: m1 });
  await api(a, `/api/topics/${t}/coauthors`, 'POST', { user_id: meB.id });
  await api(a, `/api/modules/${m2}/coauthors`, 'POST', { user_id: meC.id });
  ok(!!(m1 && m2 && t), 'Aufbau: Modul im Themenentwurf (Co-Autor am Thema), zweites Modul direkt freigegeben');

  // 1. Themenseite des Entwurfs: keine funktionslosen Lernen-Links für Co-Autoren
  await b.go(`${B}/topics/${t}`);
  ok(await b.waitText('Vorschaukarte') || await b.waitText('PvMod-'), 'Co-Autor: Themenseite des Entwurfs lädt');
  const hasLearn = await b.ev(`[...document.querySelectorAll('a')].some(x=>x.innerText.includes('📖 Lernen'))`);
  ok(!hasLearn && await b.has('🧪 Testen') && await b.has('🧪 Vorschau testen'), 'Co-Autor: kein „Lernen“-Link, dafür „Testen“ und „Vorschau testen“');

  // 2. Themenvorschau
  await b.go(`${B}/topics/${t}/preview`);
  ok(await b.waitText('Testmodus') && await b.waitText('Lernen testen'), 'Themenvorschau: Banner „Testmodus“ und Modul mit „Lernen testen“');

  // 3. Modulvorschau: Karte, Quiz, Auswertung
  await b.go(`${B}/module/${m1}/preview`);
  ok(await b.waitText('Testmodus') && await b.waitText('Vorschaukarte'), 'Modulvorschau: Testmodus-Banner und Karte des Entwurfs');
  ok(!(await b.ev(`document.body.innerText.includes('💬 Chat')`)), 'Modulvorschau: kein KI-Chat (würde Verlauf speichern)');
  await b.click('📝 Modulquiz testen'); ok(await b.waitText('Quiz starten'), 'Quizkonfiguration im Testmodus');
  await b.type('input[type=number]', '1');   // genau eine Frage: sonst ergänzt der Offline-Fallback weitere Fragen und „Abgeben“ erscheint erst auf der letzten
  await b.click('Quiz starten'); ok(await b.waitText('Welche Antwort stimmt?'), 'Quizfrage aus dem Entwurf erscheint');
  await b.clickContains('Richtig', 'button.quiz-option');
  await b.click('Abgeben & bewerten');
  ok(await b.waitText('Bestanden') && (await b.text()).includes('Testmodus'), 'Testauswertung „Bestanden“ mit Hinweis „Testmodus“ (nichts gespeichert)');
  ok((await api(b, '/api/quiz/history')).s !== 200 || !(await api(b, '/api/quiz/history')).j.includes(m1), 'Kein Ergebnis im Verlauf des Co-Autors');

  // 4. Verlassen und erneut starten: neue Vorschau
  await b.click('Fertig'); ok(await b.waitText('Modulquiz testen') && !(await b.text()).includes('Bestanden'), 'Nach „Fertig“: Auswertung geschlossen');
  await b.click('📝 Modulquiz testen'); ok(await b.waitText('Quiz starten'), 'Neue Quizkonfiguration erscheint');
  await b.type('input[type=number]', '1'); await b.click('Quiz starten');
  ok(await b.waitText('Welche Antwort stimmt?') && !(await b.text()).includes('Bestanden'), 'Erneuter Start beginnt eine neue Vorschau (ohne altes Ergebnis)');

  // 5. Entzug während geöffneter Vorschau
  await api(a, `/api/topics/${t}/coauthors/${meB.id}`, 'DELETE');
  await b.clickContains('Richtig', 'button.quiz-option'); await b.click('Abgeben & bewerten'); await b.waitText('entzogen', 6000);
  const t5 = await b.text();
  ok(t5.includes('Dein Zugriff auf diese Vorschau wurde entzogen.') && !t5.includes('Bestanden'), 'Entzug in offener Vorschau: „Dein Zugriff auf diese Vorschau wurde entzogen.“, keine Auswertung');
  await b.go(`${B}/module/${m1}/preview`);
  ok(await b.waitText('nicht gefunden') || await b.waitText('entzogen'), 'Nach dem Entzug: Modulvorschau nicht mehr erreichbar, keine Karten');
  ok(!(await b.text()).includes('Vorschaukarte'), 'Nach dem Entzug: Karteninhalt nicht sichtbar');

  // 6. Direkter Modul-Co-Autor: Modulvorschau ja, Themenvorschau nein
  await c.go(`${C}/module/${m2}/preview`);
  ok(await c.waitText('Direktkarte'), 'Direkter Modul-Co-Autor: Modulvorschau funktioniert');
  await c.go(`${C}/topics/${t}/preview`);
  ok(await c.waitText('nicht gefunden') || await c.waitText('entzogen'), 'Direktes Modulrecht gewährt keine Themenvorschau');
  await c.go(`${C}/module/${m1}/preview`);
  ok(await c.waitText('nicht gefunden') || await c.waitText('entzogen'), 'Direkter Co-Autor: fremdes Modul gesperrt');

  // 6b. Entzug des direkten Modulrechts in offener Vorschau
  await c.go(`${C}/module/${m2}/preview`); await c.waitText('Direktkarte');
  await c.click('📝 Modulquiz testen'); await c.waitText('Quiz starten'); await c.type('input[type=number]', '1'); await c.click('Quiz starten');
  ok(await c.waitText('Welche Antwort stimmt?'), 'Direkter Co-Autor: Quiz in offener Vorschau');
  await api(a, `/api/modules/${m2}/coauthors/${meC.id}`, 'DELETE');
  await c.clickContains('Richtig', 'button.quiz-option'); await c.click('Abgeben & bewerten'); await c.waitText('entzogen', 6000);
  const t6 = await c.text();
  ok(t6.includes('Dein Zugriff auf diese Vorschau wurde entzogen.') && !t6.includes('Bestanden'), 'Entzug des direkten Modulrechts in offener Vorschau: Sperrmeldung, keine Auswertung');
  await c.go(`${C}/module/${m2}/preview`);
  ok(!(await c.text()).includes('Direktkarte'), 'Nach Entzug des direkten Rechts: Karteninhalt nicht sichtbar');

  // 7. Fremder
  const o = await newPage(`${C}/auth/login?devUser=pv-outsider`); await sleep(2500);
  await o.go(`${C}/module/${m2}/preview`);
  ok(await o.waitText('nicht gefunden') && !(await o.text()).includes('Direktkarte'), 'Fremder: keine Vorschau, keine Inhalte');

  const errs = [a, b, c, o].flatMap(p => p.errors); ok(errs.length === 0, `Keine JavaScript-/Konsolenfehler (${errs.length}) ${errs.slice(0, 3).join(' | ')}`);
} catch (e) { console.log('SKRIPTFEHLER', e); await finish(1); }
await finish();
