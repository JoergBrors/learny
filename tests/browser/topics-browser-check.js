// Browserprüfung der Themenverwaltung (ADR 0027) per Chrome DevTools Protocol — nur Node-Bordmittel (Node 22+), keine Pakete.
// Aufruf: node tests/browser/topics-browser-check.js <ausgabeordner> <LearnCards.Web.dll> <Web-Projektordner>
const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');

const OUT = process.argv[2], WEB_DLL = process.argv[3], CONTENT_ROOT = process.argv[4];
const PORT = 5191, CDP = 9336, BASE = `http://127.0.0.1:${PORT}`;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const results = [];
const check = (ok, name) => { results.push({ ok, name }); console.log((ok ? 'OK   ' : 'FAIL ') + name); };

async function main() {
  fs.mkdirSync(OUT, { recursive: true });
  const dataDir = path.join(OUT, 'data'); fs.mkdirSync(dataDir, { recursive: true });
  const env = { ...process.env, ASPNETCORE_URLS: BASE, ASPNETCORE_ENVIRONMENT: 'Development', AUTH_MODE: 'dev', ADMIN_SUBJECTS: 'dev-topicowner', DB_PROVIDER: 'sqlite', SQLITE_PATH: path.join(dataDir, 'topics.db'),
    DATA_PROTECTION_DIR: path.join(dataDir, 'keys'), OPENAI_API_KEY: '', MCP_API_KEY: 'browser-test-key', MCP_TOKEN_KEY: Buffer.from('browser-test-key-0123456789abcdef').toString('base64'), ASPNETCORE_CONTENTROOT: CONTENT_ROOT };
  env.PATH = env.PATH.split(path.delimiter).filter(p => !/amazon/i.test(p)).join(path.delimiter);
  const web = spawn('dotnet', [WEB_DLL], { cwd: dataDir, env, stdio: 'ignore' });
  let chrome;
  try {
    for (let i = 0; i < 100; i++) { try { if ((await fetch(BASE + '/health')).ok) break; } catch { } await sleep(300); }
    const login = await fetch(BASE + '/auth/login?devUser=topicowner', { redirect: 'manual' });
    const cookie = login.headers.getSetCookie().map(c => c.split(';')[0]).join('; ');
    const api = (p, method = 'GET', body) => fetch(BASE + '/api' + p, { method, headers: { 'Content-Type': 'application/json', Cookie: cookie }, body: body ? JSON.stringify(body) : undefined });
    const json = async r => { const t = await r.text(); try { return JSON.parse(t); } catch { return t; } };

    // Daten: drei Module (je 16 Karten, 8 Fragen), Thema mit Prüfungen, zwei Versionen
    for (const m of ['Browser-Entra', 'Browser-Exchange', 'Browser-Teams']) {
      const cards = Array.from({ length: 16 }, (_, i) => ({ module: m, category: 'K', term: `${m}-${i}`, question: `Frage ${i}?`, definition: `Definition ${i}`,
        ...(i < 8 ? { quiz: [{ type: 'single_choice', question: `${m} Frage ${i}?`, options: ['A', 'B', 'C', 'D'], correct_index: 1, explanation: 'B' }] } : {}) }));
      await api('/import/cards', 'POST', { cards });
    }
    const mods = Object.fromEntries((await json(await api('/modules/'))).map(m => [m.name, m.id]));
    const topic = await json(await api('/topics/', 'POST', { name: 'Browser M365 Grundlagen', description: 'Lernpfad für den Browsertest' }));
    for (const m of ['Browser-Entra', 'Browser-Exchange', 'Browser-Teams']) await api(`/topics/${topic.id}/modules`, 'POST', { module_id: mods[m] });
    for (let i = 0; i < 3; i++) await api(`/topics/${topic.id}/questions`, 'POST', { type: 'single_choice', question: `Themenfrage ${i}?`, options: ['x', 'y', 'z'], correct_indexes: [1], explanation: 'y' });
    const exam = { enabled: true, question_count: 3, pass_percent: 60, allow_retry: true, single_choice: true, multiple_response: true, free_text: false };
    await api(`/topics/${topic.id}/exam-config`, 'PUT', { module_exams: Object.fromEntries(Object.values(mods).map(id => [id, exam])),
      final_exam: { enabled: true, question_count: 15, pass_percent: 60, distribution: 'even', single_choice: true, multiple_response: true, free_text: false, unlock: { require_all_modules_completed: false, require_module_exams_passed: false } } });
    const v1 = await api(`/topics/${topic.id}/versions`, 'POST', {});
    check(v1.status === 201, 'Seed: Version 1 veröffentlicht');

    const chromePath = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find(fs.existsSync);
    chrome = spawn(chromePath, ['--headless=new', `--remote-debugging-port=${CDP}`, `--user-data-dir=${path.join(OUT, 'profile')}`, '--no-first-run', '--disable-gpu', 'about:blank'], { stdio: 'ignore' });
    let target;
    for (let i = 0; i < 50; i++) { try { const t = await (await fetch(`http://127.0.0.1:${CDP}/json/list`)).json(); target = t.find(x => x.type === 'page'); if (target) break; } catch { } await sleep(300); }
    const ws = new WebSocket(target.webSocketDebuggerUrl);
    await new Promise(r => ws.addEventListener('open', r));
    let msgId = 0; const pending = new Map(); const consoleErrors = [];
    ws.addEventListener('message', ev => {
      const m = JSON.parse(ev.data);
      if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
      if (m.method === 'Runtime.exceptionThrown') consoleErrors.push(m.params.exceptionDetails.text + ' ' + (m.params.exceptionDetails.exception?.description || ''));
      if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'log' && String(m.params.args[0]?.value).startsWith('OVERFLOW')) console.log(m.params.args[0].value);
      if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') consoleErrors.push(m.params.args.map(a => a.value || a.description).join(' '));
    });
    const send = (method, params = {}) => new Promise(res => { const id = ++msgId; pending.set(id, res); ws.send(JSON.stringify({ id, method, params })); });
    const ev = async expr => { const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }); if (r.result.exceptionDetails) throw new Error(r.result.exceptionDetails.text); return r.result.result.value; };
    const waitFor = async (expr, ms = 15000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { try { if (await ev(expr)) return true; } catch { } await sleep(150); } return false; };
    const shot = async name => { const r = await send('Page.captureScreenshot', { format: 'png' }); fs.writeFileSync(path.join(OUT, name + '.png'), Buffer.from(r.result.data, 'base64')); };
    const q = s => JSON.stringify(s);
    const click = (sel, text) => ev(`(() => { const e = [...document.querySelectorAll(${q(sel)})].find(x => x.offsetParent !== null && x.textContent.trim().includes(${q(text)})); if (!e) return false; e.click(); return true; })()`);
    const hScroll = () => ev(`(() => { const w = document.documentElement.clientWidth; const bad = [...document.querySelectorAll('body *')].filter(e => !e.closest('.table-wrap') && e.getBoundingClientRect().right > w + 1).slice(0, 3).map(e => e.tagName + '.' + e.className); if (!bad.length) return false; console.log('OVERFLOW ' + w + ' ' + bad.join(',')); return true; })()`);
    const setTheme = t => ev(`document.documentElement.setAttribute('data-theme', '${t}')`);
    const setViewport = (w, h, mobile) => send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 1, mobile });
    const go = async url => { await send('Page.navigate', { url: BASE + url }); await sleep(2000); };

    await send('Page.enable'); await send('Runtime.enable');
    await setViewport(1280, 900, false);
    await send('Page.navigate', { url: BASE + '/auth/login?devUser=topicowner' }); await sleep(1500);

    // ── Navigation: Module und Themen sind getrennte Bereiche (ADR 0028)
    const sub = await json(await api(`/sharing/modules/${mods['Browser-Teams']}/submissions`, 'POST'));
    const approved = (await api(`/admin/submissions/${sub.id}/approve`, 'POST')).status;
    check(approved === 204, 'Seed: globales Modul veröffentlicht');
    await go('/');
    check(await waitFor(`document.querySelector('h1')?.textContent.trim() === 'Module'`), 'Modulübersicht trägt die Überschrift „Module“');
    check(await ev(`(() => { const l = [...document.querySelectorAll('.topbar-actions a')].map(a => a.textContent.trim() + '=' + a.getAttribute('href')); return l.includes('Module=/') && l.includes('Themen=/topics'); })()`), 'Navigation: „Module“ → / und „Themen“ → /topics');
    await shot('00-module');
    check(await click('.tab-row .chip', 'Globale Module'), 'Reiter „Globale Module“ öffnen');
    check(await waitFor(`[...document.querySelectorAll('button')].some(b => b.textContent.includes('In eigene Module klonen'))`), 'Globale Module bieten „In eigene Module klonen“ an');
    check(await click('button', 'In eigene Module klonen'), 'Globales Modul klonen');
    check(await waitFor(`/geklont/.test(document.querySelector('.alert')?.textContent || '')`), 'Klonen bestätigt die eigene Kopie');
    check(await waitFor(`[...document.querySelectorAll('.module-card h2')].some(h => h.textContent.includes('Browser-Teams (Kopie)'))`), 'Der Klon erscheint unter „Meine Module“');
    check(await ev(`[...document.querySelectorAll('a')].some(a => (a.getAttribute('href') || '').includes('/export') && a.textContent.includes('JSON'))`), 'Module bieten „Als JSON exportieren“');
    await go('/topics');
    check(await waitFor(`document.querySelector('h1')?.textContent.trim() === 'Themen'`) && await ev(`!![...document.querySelectorAll('label.file-btn')].find(l => l.textContent.includes('Thema aus JSON erstellen'))`), 'Themenübersicht: eigener Bereich mit „Thema aus JSON erstellen“');

    // ── Übersicht
    await go('/topics');
    check(await waitFor(`document.querySelectorAll('.topic-card').length === 1`), 'Themenübersicht zeigt das Thema als Karte');
    const card = await ev(`document.querySelector('.topic-card').textContent`);
    check(/48\s*Lernkarten/.test(card) && /24\s*Multiple-Choice-Fragen/.test(card) && /15\s*Abschlussprüfungsfragen/.test(card) && /Version 1/.test(card), 'Themenkarte: 48 Lernkarten, 24 Multiple-Choice-Fragen, 15 Abschlussprüfungsfragen, Version 1');
    check(await ev(`[...document.querySelectorAll('[role=tab]')].map(t => t.textContent).join('|')`).then(t => /Meine Themen/.test(t) && /Mit mir geteilt/.test(t) && /Mir zugewiesen/.test(t) && /Globale Themen/.test(t)), 'Vier Bereiche: meine, geteilte, zugewiesene und globale Themen');
    await shot('01-uebersicht');

    // ── Detail und Modulprüfung durchführen
    await go(`/topics/${topic.id}?version=1`);
    check(await waitFor(`document.querySelectorAll('.topic-module').length === 3`), 'Themenseite listet die drei Module im Lernpfad');
    check(await waitFor(`!!document.querySelector('.topic-final')`), 'Abschlussprüfung ist Teil des Lernpfads');
    check(await ev(`[...document.querySelectorAll('.topic-module a')].filter(x => x.textContent.includes('Lernen')).every(x => x.getAttribute('href').startsWith('/module/'))`), 'Themenseite: „Lernen“ verweist auf die Modulseite (/module/{id})');
    const learnHref = await ev(`[...document.querySelectorAll('.topic-module a')].find(x => x.textContent.includes('Lernen')).getAttribute('href')`);
    await go(learnHref);
    check(await waitFor(`!/Seite nicht gefunden/.test(document.body.textContent) && !!document.querySelector('h1, .module-hero')`), 'Lernen aus dem Thema öffnet das Modul (kein „Seite nicht gefunden“)');
    await go(`/topics/${topic.id}?version=1`);
    await shot('02-thema');
    const started = await click('.topic-module .btn-primary', 'Modulprüfung starten');
    if (!started) console.log('DEBUG ' + await ev(`document.querySelector('.topic-actions')?.outerHTML.slice(0,400) + ' | ' + document.querySelector('.topic-module')?.textContent.slice(0,300)`));
    check(started, 'Modulprüfung starten');
    check(await waitFor(`!!document.querySelector('.quiz-question')`), 'Prüfung zeigt Fragen (QuizRunner der bestehenden Engine)');
    for (let i = 0; i < 3; i++) {
      await ev(`(() => { const o = document.querySelectorAll('.quiz-option')[1]; if (o) o.click(); })()`); await sleep(250);
      if (i < 2) await click('button', 'Weiter'); else await click('button', 'Abgeben');
      await sleep(400);
    }
    check(await waitFor(`/bestanden|Bestanden|100/.test(document.querySelector('.card-quiz-runner').textContent)`), 'Modulprüfung wird serverseitig bewertet und angezeigt');
    await shot('03-pruefung-ergebnis');
    await click('button', 'Fertig'); await sleep(900);
    check(await ev(`document.querySelector('.card.panel').textContent.includes('Modulprüfungen')`) && await ev(`/Modulprüfungen:\\s*1 von 3/.test(document.body.textContent)`), 'Fortschritt: 1 von 3 Modulprüfungen bestanden');

    // ── Editor: Ordnung, Zahlen, Veröffentlichung
    await go(`/topics/${topic.id}/edit`);
    check(await waitFor(`document.querySelectorAll('.topic-module-row').length === 3`), 'Editor zeigt die Module mit Sortierung');
    const first = await ev(`document.querySelector('.topic-module-row strong').textContent`);
    await click('.topic-module-row .icon-btn', '▼'); await sleep(900);
    check(await ev(`document.querySelector('.topic-module-row strong').textContent`) !== first, 'Modulreihenfolge lässt sich per Schaltfläche ändern');
    check(await ev(`/unveröffentlichte Änderungen/.test(document.body.textContent)`), 'Editor erkennt unveröffentlichte Änderungen');
    check(await ev(`/48\\s*Lernkarten/.test(document.body.textContent) && /24\\s*Multiple-Choice-Fragen/.test(document.body.textContent) && /15\\s*Abschlussprüfungsfragen/.test(document.body.textContent)`), 'Editor-Kopf zeigt 48 / 24 / 15 automatisch');
    check(await ev(`/Thema als JSON/.test(document.body.textContent) && !![...document.querySelectorAll('a')].find(x => x.textContent.includes('Thema als JSON exportieren'))`) && await ev(`!![...document.querySelectorAll('label.file-btn')].find(l => l.textContent.includes('Thema aus JSON aktualisieren'))`), 'Editor: JSON-Export und -Aktualisierung vorhanden');
    check(await ev(`/nur eigene, private Module/.test(document.body.textContent)`), 'Editor: Modulauswahl nur aus eigenen Modulen');
    await click('button', 'Neue Version veröffentlichen'); await sleep(1500);
    check(await ev(`/Neue Version veröffentlicht/.test(document.body.textContent) && !/unveröffentlichte Änderungen/.test(document.body.textContent)`), 'Veröffentlichen erzeugt Version 2; Entwurf entspricht ihr');
    check(await ev(`document.querySelectorAll('.table tbody tr').length >= 2`), 'Versionshistorie zeigt beide Versionen');
    await shot('04-editor');

    // ── Responsive, Dark Mode, Tastatur
    for (const [name, w, h, mobile] of [['desktop', 1280, 900, false], ['mobil', 390, 800, true]]) {
      await setViewport(w, h, mobile);
      for (const theme of ['light', 'dark']) {
        await setTheme(theme);
        for (const url of ['/', '/topics', `/topics/${topic.id}`, `/topics/${topic.id}/edit`]) {
          await go(url); await setTheme(theme); await sleep(700);
          check(!(await hScroll()), `${name}/${theme}: ${url.replace(topic.id, '{id}')} ohne horizontales Scrollen`);
        }
      }
    }
    await setViewport(390, 800, true); await setTheme('dark'); await go(`/topics/${topic.id}`); await shot('05-mobil-dunkel');
    await setViewport(1280, 900, false);
    await go('/topics');
    await ev(`document.querySelector('.topic-card').focus()`);
    console.log('FOCUS ' + await ev(`document.activeElement.className + ' ' + getComputedStyle(document.activeElement).outlineStyle + ' ' + getComputedStyle(document.activeElement).boxShadow`));
    check(await ev(`document.activeElement.classList.contains('topic-card') && (getComputedStyle(document.activeElement).outlineStyle !== 'none' || getComputedStyle(document.activeElement).boxShadow !== 'none')`), 'Tastatur: Themenkarte ist fokussierbar und zeigt einen Fokusrahmen');
    // ── JSON-Import/-Export über die Oberfläche, Referenzfehler, Hilfeseite (ADR 0028)
    const upload = async (selector, file) => {
      const doc = await send('DOM.getDocument', { depth: -1 });
      const q = await send('DOM.querySelector', { nodeId: doc.result.root.nodeId, selector });
      if (!q.result.nodeId) return false;
      await send('DOM.setFileInputFiles', { files: [file], nodeId: q.result.nodeId });
      return true;
    };
    const writeJson = (name, obj) => { const f = path.join(OUT, name); fs.writeFileSync(f, JSON.stringify(obj, null, 2)); return f; };
    const importRef = '5b0e2d7c-1f3a-4a8e-9c64-0d2f7e8a1b33';
    const modFile = writeJson('modul-import.json', { format: 'learny-module', schema_version: 1, reference_id: importRef, name: 'Browser-Import-Modul', cards: [{ category: 'K', term: 'Import-1', question: 'Frage?', definition: 'Definition' }, { category: 'K', term: 'Import-2', question: 'Frage 2?', definition: 'Definition 2' }] });
    await go('/');
    check(await waitFor(`!!document.querySelector('label.file-btn input[type=file]')`), 'Home: Schaltfläche „JSON importieren“ vorhanden');
    check(await upload('label.file-btn input[type=file]', modFile), 'Modul-JSON auswählen');
    check(await waitFor(`/Modul-Import/.test(document.body.textContent) && /Browser-Import-Modul/.test(document.body.textContent)`), 'Modul-JSON wird erkannt und als Vorschau (Modul-Import) angezeigt, noch nichts importiert');
    check(await click('button', 'Import bestätigen'), 'Modul-Import bestätigen');
    check(await waitFor(`/Modul „Browser-Import-Modul“ importiert/.test(document.body.textContent)`), 'Modul-Import meldet Erfolg');
    check(await waitFor(`[...document.querySelectorAll('.module-card h2')].some(h => h.textContent.trim() === 'Browser-Import-Modul')`), 'Der importierte Modulname wird unverändert dargestellt');
    await shot('06-modul-import');
    const badModFile = writeJson('modul-kaputt.json', { format: 'learny-module', schema_version: 1, reference_id: 'keine-guid', name: 'Kaputt', cards: [] });
    await upload('label.file-btn input[type=file]', badModFile);
    check(await waitFor(`/GUID/.test(document.querySelector('.alert')?.textContent || '')`), 'Ungültige reference_id: verständliche Fehlermeldung statt Import');

    const topicFile = writeJson('thema-import.json', { format: 'learny-topic', schema_version: 1, name: 'Browser-JSON-Thema', modules: [{ module_ref: importRef, module_name: 'Beliebiger Anzeigename' }] });
    await go('/topics');
    check(await upload('label.file-btn input[type=file]', topicFile), 'Thema-JSON auswählen');
    check(await waitFor(`location.pathname.endsWith('/edit')`), 'Thema aus JSON wird als Entwurf angelegt und im Editor geöffnet (Referenz löst trotz abweichendem Anzeigenamen auf)');
    check(await waitFor(`[...document.querySelectorAll('.topic-module-row strong')].some(x => x.textContent.trim() === 'Browser-Import-Modul')`), 'Editor zeigt den tatsächlichen Modulnamen, nicht den Anzeigenamen aus der Datei');
    const badTopicFile = writeJson('thema-falsche-ref.json', { format: 'learny-topic', schema_version: 1, name: 'Browser-JSON-Thema', modules: [{ module_ref: '9c9c9c9c-1111-4222-8333-444455556666', module_name: 'Browser-Import-Modul' }] });
    const inputs = `[...document.querySelectorAll('label.file-btn')].find(l => l.textContent.includes('Thema aus JSON aktualisieren'))`;
    await ev(`${inputs}.querySelector('input').setAttribute('id', 'upd-json')`);
    check(await upload('#upd-json', badTopicFile), 'Thema-JSON zur Aktualisierung auswählen');
    check(await waitFor(`/Entwurf aktualisieren/.test(document.body.textContent)`), 'Gültige Datei: Bestätigung „Entwurf aktualisieren“ erscheint');
    await click('button', 'Entwurf aktualisieren');
    check(await waitFor(`/module_ref/.test(document.querySelector('.alert-error')?.textContent || '')`), 'Unbekannte module_ref: Fehlermeldung nennt die Referenz, der Entwurf bleibt unverändert');
    check(await ev(`[...document.querySelectorAll('.topic-module-row strong')].some(x => x.textContent.trim() === 'Browser-Import-Modul')`), 'Entwurf nach dem Fehler unverändert (Modul weiterhin zugeordnet)');
    const invalidGuid = writeJson('thema-ungueltig.json', { format: 'learny-topic', schema_version: 1, name: 'X', modules: [{ module_ref: 'abc', module_name: 'Y' }] });
    await upload('#upd-json', invalidGuid);
    check(await waitFor(`/GUID/.test(document.querySelector('.alert-error')?.textContent || '')`), 'Ungültige module_ref (keine GUID) wird sofort mit verständlicher Meldung abgewiesen');
    await shot('07-referenzfehler');

    await go('/help');
    check(await waitFor(`!!document.querySelector('#topics')`), 'Hilfeseite: Abschnitt „Module und Themen“ vorhanden');
    const helpLinks = await ev(`[...document.querySelectorAll('a[href^="/templates/"]')].map(a => a.getAttribute('href'))`);
    let helpOk = helpLinks.length >= 6;
    for (const l of helpLinks) { const r = await fetch(BASE + l, { headers: { Cookie: cookie } }); try { JSON.parse(await r.text()); } catch { helpOk = false; } if (!r.ok) helpOk = false; }
    check(helpOk && helpLinks.some(l => l.includes('module-template')) && helpLinks.some(l => l.includes('topic-template')), 'Hilfeseite: alle Vorlagen-Links (inkl. Modul- und Themenvorlage) liefern gültiges JSON');
    const tplMod = await (await fetch(BASE + '/templates/module-template.json', { headers: { Cookie: cookie } })).json();
    const tplTop = await (await fetch(BASE + '/templates/topic-template.json', { headers: { Cookie: cookie } })).json();
    const impM = await api('/modules/import', 'POST', tplMod); const impT = await api('/topics/import', 'POST', tplTop);
    check(impM.status === 201 && impT.status === 201, 'Hilfeseite: Beispielvorlagen (Modul, Thema) sind importierbar und aufeinander abgestimmt (module_ref)');
    check(await ev(`/module_ref/.test(document.body.textContent) && /Anzeigename/.test(document.body.textContent)`), 'Hilfeseite erklärt module_ref und Anzeigename');

    check(consoleErrors.length === 0, 'Keine Konsolenfehler (' + consoleErrors.slice(0, 3).join(' | ') + ')');
  } finally {
    try { chrome?.kill(); } catch { }
    try { web.kill(); } catch { }
  }
  const failed = results.filter(r => !r.ok).length;
  console.log(`\n${results.length - failed}/${results.length} Browserprüfungen bestanden` + (failed ? `, ${failed} FEHLGESCHLAGEN` : ''));
  process.exit(failed ? 1 : 0);
}
main().catch(e => { console.error('ABGEBROCHEN: ' + e); process.exit(2); });
