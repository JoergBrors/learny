// Browserprüfung des Karten-/Quiz-Editors (ADR 0020) per Chrome DevTools Protocol — nur Node-Bordmittel (Node 22+), keine Pakete.
// Aufruf: node tests/browser/editor-browser-check.js <ausgabeordner> <LearnCards.Web.dll> <Web-Projektordner>
const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');

const OUT = process.argv[2], WEB_DLL = process.argv[3], CONTENT_ROOT = process.argv[4];
const PORT = 5189, CDP = 9334, BASE = `http://127.0.0.1:${PORT}`;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const results = [];
const check = (ok, name) => { results.push({ ok, name }); console.log((ok ? 'OK   ' : 'FAIL ') + name); };
// 32x16-PNG (gleiches Beispielbild wie in den Vorlagen)
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAACAAAAAQCAIAAAD4YuoOAAAAI0lEQVR42mPg94mhKWIAYq3ls2mERi0YtWDUglELRpIFNEUAOleXE3NVou0AAAAASUVORK5CYII=', 'base64');

async function main() {
  fs.mkdirSync(OUT, { recursive: true });
  const dataDir = path.join(OUT, 'data'); fs.mkdirSync(dataDir, { recursive: true });
  const pngPath = path.join(OUT, 'beispiel.png'); fs.writeFileSync(pngPath, PNG);
  const env = { ...process.env, ASPNETCORE_URLS: BASE, ASPNETCORE_ENVIRONMENT: 'Development', AUTH_MODE: 'dev', DB_PROVIDER: 'sqlite', SQLITE_PATH: path.join(dataDir, 'e.db'),
    DATA_PROTECTION_DIR: path.join(dataDir, 'keys'), OPENAI_API_KEY: '', MCP_API_KEY: 'browser-test-key', ASPNETCORE_CONTENTROOT: CONTENT_ROOT };
  env.PATH = env.PATH.split(path.delimiter).filter(p => !/amazon/i.test(p)).join(path.delimiter);
  const web = spawn('dotnet', [WEB_DLL], { cwd: dataDir, env, stdio: 'ignore' });
  let chrome;
  try {
    for (let i = 0; i < 100; i++) { try { if ((await fetch(BASE + '/health')).ok) break; } catch { } await sleep(300); }
    const login = await fetch(BASE + '/auth/login?devUser=editor', { redirect: 'manual' });
    const cookie = login.headers.getSetCookie().map(c => c.split(';')[0]).join('; ');
    const api = (p, method = 'GET', body, headers = {}) => fetch(BASE + '/api' + p, { method, headers: { 'Content-Type': 'application/json', Cookie: cookie, ...headers }, body: body ? JSON.stringify(body) : undefined });
    const seed = async c => (await (await api('/cards/', 'POST', c)).json());
    const c1 = await seed({ module: 'Editor-Test', category: 'Grundlagen', term: 'Erste Karte', question: 'Was ist die erste Karte?', definition: 'Einfacher **Text**.',
      quiz: [{ type: 'single_choice', question: 'Feste Frage?', options: ['a', 'b', 'c', 'd'], correct_index: 2, explanation: 'c' }] });
    const c2 = await seed({ module: 'Editor-Test', category: 'Grundlagen', term: 'Zweite Karte', question: 'Zweite Frage?', definition: 'Noch ein Text.' });
    const moduleId = (await (await api('/cards/' + c1.id)).json()).module_id;

    const chromePath = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find(fs.existsSync);
    chrome = spawn(chromePath, ['--headless=new', `--remote-debugging-port=${CDP}`, `--user-data-dir=${path.join(OUT, 'profile')}`, '--no-first-run', '--disable-gpu', 'about:blank'], { stdio: 'ignore' });
    let target;
    for (let i = 0; i < 50; i++) { try { const t = await (await fetch(`http://127.0.0.1:${CDP}/json/list`)).json(); target = t.find(x => x.type === 'page'); if (target) break; } catch { } await sleep(300); }
    const ws = new WebSocket(target.webSocketDebuggerUrl);
    await new Promise(r => ws.addEventListener('open', r));
    let msgId = 0; const pending = new Map(); const consoleErrors = []; let dialogs = []; let acceptDialogs = true;
    ws.addEventListener('message', ev => {
      const m = JSON.parse(ev.data);
      if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
      if (m.method === 'Runtime.exceptionThrown') consoleErrors.push(m.params.exceptionDetails.text + ' ' + (m.params.exceptionDetails.exception?.description || ''));
      if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') consoleErrors.push(m.params.args.map(a => a.value || a.description).join(' '));
      if (m.method === 'Page.javascriptDialogOpening') { dialogs.push(m.params.message); send('Page.handleJavaScriptDialog', { accept: acceptDialogs }); }
    });
    const send = (method, params = {}) => new Promise(res => { const id = ++msgId; pending.set(id, res); ws.send(JSON.stringify({ id, method, params })); });
    const ev = async expr => { const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }); if (r.result.exceptionDetails) throw new Error(r.result.exceptionDetails.exception?.description || 'eval'); return r.result.result.value; };
    const waitFor = async (expr, label, ms = 15000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { try { if (await ev(expr)) return true; } catch { } await sleep(150); } console.log('  (Timeout: ' + label + ')'); return false; };
    const shot = async name => { const r = await send('Page.captureScreenshot', { format: 'png' }); fs.writeFileSync(path.join(OUT, name + '.png'), Buffer.from(r.result.data, 'base64')); };
    const q = s => JSON.stringify(s);
    const click = (sel, text) => ev(`(() => { const e = [...document.querySelectorAll(${q(sel)})].find(x => x.offsetParent !== null && x.textContent.trim().includes(${q(text)})); if (!e) return false; e.click(); return true; })()`);
    const setValue = (sel, value, evt = 'input', nth = 0) => ev(`(() => { const e = document.querySelectorAll(${q(sel)})[${nth}]; if (!e) return false; e.value = ${q(value)}; e.dispatchEvent(new Event(${q(evt)}, { bubbles: true })); return true; })()`);
    const labelInput = (text, value, evt = 'input', tag = 'input') => ev(`(() => { const l = [...document.querySelectorAll('.editor-main label')].find(x => x.textContent.trim().startsWith(${q(text)})); const e = l && l.querySelector(${q(tag)}); if (!e) return false; e.value = ${q(value)}; e.dispatchEvent(new Event(${q(evt)}, { bubbles: true })); return true; })()`);
    const setViewport = (w, h, mobile) => send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 1, mobile });
    const setTheme = t => ev(`document.documentElement.setAttribute('data-theme', '${t}')`);
    const hScroll = () => ev('document.documentElement.scrollWidth > document.documentElement.clientWidth + 1');
    const state = () => ev(`(document.querySelector('.editor-state') || {}).textContent || ''`);
    const surfaceIndex = label => ev(`[...document.querySelectorAll('.rte')].findIndex(r => r.querySelector('label').textContent.includes(${q(label)}))`);
    const typeInSurface = async (idx, text) => {
      await ev(`(() => { const s = document.querySelectorAll('.rte-surface')[${idx}]; s.focus(); const r = document.createRange(); r.selectNodeContents(s); r.collapse(false); const sel = getSelection(); sel.removeAllRanges(); sel.addRange(r); })()`);
      await send('Input.insertText', { text });
      await sleep(350);
    };
    const selectAllIn = idx => ev(`(() => { const s = document.querySelectorAll('.rte-surface')[${idx}]; s.focus(); document.execCommand('selectAll'); })()`);
    const toolbar = (idx, kind) => ev(`(() => { const b = document.querySelectorAll('.rte')[${idx}].querySelector('[data-rte=${kind}]'); b.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true })); b.click(); return true; })()`);

    await send('Page.enable'); await send('Runtime.enable'); await send('DOM.enable');
    await setViewport(1280, 900, false);
    await send('Page.navigate', { url: BASE + '/auth/login?devUser=editor' }); await sleep(1500);
    await send('Page.navigate', { url: `${BASE}/module/${moduleId}/edit` });
    check(await waitFor(`!!document.querySelector('.editor-layout') && document.querySelectorAll('.editor-card').length >= 2`, 'Editor'), 'Editor lädt und listet die Karten (Desktop)');
    await sleep(2500);
    await shot('01-editor-leer');

    // ── Neue Karte anlegen
    check(await click('button', '+ Neue Karte'), 'Neue Karte beginnen');
    check(await waitFor(`!!document.querySelector('.editor-form')`, 'Formular'), 'Formular erscheint');
    check((await state()).includes('Neue Karte'), 'Neue Karte: Status „Neue Karte“');
    await labelInput('Kategorie', 'Editor-Kategorie'); await labelInput('Begriff', 'Editor-Begriff');
    await labelInput('Frage auf der Kartenvorderseite', 'Wie funktioniert der Editor?', 'input', 'textarea');
    await sleep(300);
    check((await state()).includes('Ungespeicherte'), 'Änderungen werden als „ungespeichert“ erkannt');

    // ── Textformatierung
    const defIdx = await surfaceIndex('Antwort / Definition');
    await typeInSurface(defIdx, 'Fetter Text und ein Link');
    await selectAllIn(defIdx);
    await toolbar(defIdx, 'bold'); await sleep(400);
    check(await ev(`/<(b|strong)>/.test(document.querySelectorAll('.rte-surface')[${defIdx}].innerHTML)`), 'Textformatierung: Fett im Editor sichtbar');
    await ev(`(() => { const s = document.querySelectorAll('.rte-surface')[${defIdx}]; s.focus(); const r = document.createRange(); r.selectNodeContents(s); r.collapse(false); getSelection().removeAllRanges(); getSelection().addRange(r); })()`);
    await send('Input.insertText', { text: ' ' }); await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r' }); await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 });
    await send('Input.insertText', { text: 'Erster Punkt' }); await toolbar(defIdx, 'ul'); await sleep(300);
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r' }); await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 });
    await send('Input.insertText', { text: 'Zweiter Punkt' }); await sleep(400);
    check(await ev(`document.querySelectorAll('.rte-surface')[${defIdx}].querySelectorAll('li').length === 2`), 'Textformatierung: Aufzählung mit zwei Punkten');
    // Link: erstes Wort markieren
    await ev(`(() => { const s = document.querySelectorAll('.rte-surface')[${defIdx}]; s.focus(); const t = document.createTreeWalker(s, NodeFilter.SHOW_TEXT).nextNode(); const r = document.createRange(); r.setStart(t, 0); r.setEnd(t, 5); getSelection().removeAllRanges(); getSelection().addRange(r); })()`);
    await sleep(200);
    await toolbar(defIdx, 'link'); await sleep(300);
    await setValue('.rte-link input', 'javascript:alert(1)'); await click('.rte-link .btn-primary', 'Link setzen'); await sleep(300);
    check(await ev(`!!document.querySelector('.rte-error')`), 'Link: gefährliche Adresse (javascript:) wird abgelehnt');
    await setValue('.rte-link input', 'https://learn.microsoft.com/'); await click('.rte-link .btn-primary', 'Link setzen'); await sleep(500);
    check(await ev(`!!document.querySelectorAll('.rte-surface')[${defIdx}].querySelector('a[href^="https://learn.microsoft.com"]')`), 'Link: https-Adresse wird gesetzt');
    await shot('02-editor-formatiert');

    // ── Vorschau
    check(await click('.editor-bar .btn', 'Vorschau'), 'Vorschau öffnen');
    check(await waitFor(`!!document.querySelector('.editor-preview .rt')`, 'Vorschau'), 'Vorschau zeigt den formatierten Text');
    if (process.env.DEBUG_EDITOR) console.log('  DEBUG preview=', await ev(`document.querySelector('.editor-preview').innerHTML.slice(0,600)`));
    check(await ev(`(() => { const p = document.querySelector('.editor-preview'); return !!p.querySelector('strong') && !!p.querySelector('ul li') && !!p.querySelector('a[rel*=noopener]'); })()`), 'Vorschau: fett, Liste und Link (rel=noopener) werden serverseitig gerendert');
    await shot('03-vorschau');
    await click('.editor-bar .btn', 'Bearbeiten'); await sleep(500);
    check(await ev(`document.querySelectorAll('.rte-surface')[${await surfaceIndex('Antwort / Definition')}].innerHTML.includes('<li>')`), 'Zurück zum Bearbeiten: Inhalt bleibt erhalten');

    // ── Bild hochladen
    const doc = await send('DOM.getDocument', { depth: -1 });
    const fileNode = await send('DOM.querySelector', { nodeId: doc.result.root.nodeId, selector: 'input[type=file]' });
    await send('DOM.setFileInputFiles', { files: [pngPath], nodeId: fileNode.result.nodeId });
    check(await waitFor(`!!document.querySelector('.editor-image img')`, 'Bild'), 'Bild hochladen: Vorschau erscheint');
    await setValue('.editor-image-fields input', 'Farbige Beispielfläche', 'input', 0);
    await setValue('.editor-image-fields input', 'Beispielbild des Editors', 'input', 1);
    await sleep(300);

    // ── Feste Quizfrage (Multiple Response)
    await click('button', '+ Feste Frage'); await sleep(400);
    await setValue('.editor-question select', 'multiple_response', 'change'); await sleep(400);
    await setValue('.editor-question textarea', 'Welche sind Primzahlen?', 'change', 0);
    const opts = ['2', '3', '4', '6'];
    for (let i = 0; i < 4; i++) await setValue('.editor-question .qform-option .input', opts[i], 'change', i);
        await ev(`document.querySelectorAll('.editor-question .qform-option input[type=checkbox]')[1].click()`); await sleep(400);
    await setValue('.editor-question textarea', '2 und 3 sind Primzahlen.', 'change', 1);
    await sleep(300);
    check(await ev(`document.querySelectorAll('.editor-question .qform-option input[type=checkbox]:checked').length === 2`), 'Feste Frage: Multiple Response mit zwei richtigen Antworten');
    await shot('04-editor-bild-quiz');

    // ── Speichern
    check(await click('.editor-bar .btn-primary', 'Speichern'), 'Speichern');
    check(await waitFor(`!!document.querySelector('.alert-ok') && document.querySelector('.editor-state-clean')`, 'Gespeichert'), 'Gespeichert: Bestätigung, Status „Gespeichert“ (keine ungespeicherten Änderungen)');
    const cards = await (await api(`/cards/?module_id=${moduleId}`)).json();
    const saved = cards.find(c => c.term === 'Editor-Begriff');
    if (process.env.DEBUG_EDITOR) console.log('  DEBUG def=', JSON.stringify(saved && saved.definition), 'quiz=', JSON.stringify(saved && saved.quiz));
    check(!!saved && saved.definition.includes('**') && saved.definition.includes('](https://learn.microsoft.com/)') && saved.definition.includes('- '), 'API: Formatierung wurde als begrenztes Markup gespeichert (fett, Link, Liste) — kein HTML');
    check(!!saved && !/<[a-z]/i.test(saved.definition), 'API: gespeicherter Text enthält kein HTML');
    check(!!saved && saved.images.length === 1 && saved.images[0].alt_text === 'Farbige Beispielfläche' && saved.images[0].caption === 'Beispielbild des Editors', 'API: Bild mit Alternativtext und Unterschrift gespeichert');
    const imgRes = saved ? await fetch(BASE + saved.images[0].url, { headers: { Cookie: cookie } }) : null;
    check(!!imgRes && imgRes.ok && imgRes.headers.get('content-type') === 'image/png', 'Bildauslieferung über /api/images/{id}');
    check(!!saved && saved.quiz.length === 1 && saved.quiz[0].type === 'multiple_response' && JSON.stringify(saved.quiz[0].correct_indexes) === '[0,1]', 'API: feste Quizfrage (quiz[]) gespeichert');

    // ── Bestehende Bilder bleiben beim erneuten Speichern erhalten; Texte änderbar
    await labelInput('Begriff', 'Editor-Begriff geändert'); await sleep(300);
    await click('.editor-bar .btn-primary', 'Speichern'); await sleep(1500);
    const after = (await (await api(`/cards/?module_id=${moduleId}`)).json()).find(c => c.id === saved.id);
    check(after.term === 'Editor-Begriff geändert' && after.images.length === 1 && after.images[0].image_id === saved.images[0].image_id, 'Erneutes Speichern behält das vorhandene Bild (gleiche image_id)');

    // ── Fragenpool: Editor-Frage
    await click('button', '+ Neue Frage im Fragenpool'); await sleep(400);
    const poolForm = `.editor-section:last-of-type .editor-question`;
    await setValue(`${poolForm} textarea`, 'Pool-Frage aus dem Editor?', 'change', 0);
    for (let i = 0; i < 4; i++) await setValue(`${poolForm} .qform-option .input`, ['rot', 'grün', 'blau', 'gelb'][i], 'change', i);
    await ev(`document.querySelectorAll(${q(poolForm + ' .qform-option input[type=radio]')})[2].click()`); await sleep(200);
    await setValue(`${poolForm} textarea`, 'Blau ist richtig.', 'change', 1);
    await click('button', 'Frage speichern'); await sleep(1200);
    const pool = await (await api(`/modules/${moduleId}/quiz-questions`)).json();
    const poolQ = pool.find(p => p.question === 'Pool-Frage aus dem Editor?');
    check(!!poolQ && poolQ.source === 'editor' && poolQ.card_id === saved.id && JSON.stringify(poolQ.correct_indexes) === '[2]', 'Fragenpool: Editor-Frage angelegt (source=editor, der Karte zugeordnet)');
    await click('button', 'Deaktivieren'); await sleep(800);
    check((await (await api(`/modules/${moduleId}/quiz-questions?active=false`)).json()).some(p => p.id === poolQ.id), 'Fragenpool: Frage deaktiviert');
    await click('button', 'Aktivieren'); await sleep(600);
    // Karte speichern ändert die Pool-Frage nicht
    await labelInput('Begriff', 'Editor-Begriff zwei'); await sleep(200); await click('.editor-bar .btn-primary', 'Speichern'); await sleep(1500);
    check((await (await api(`/modules/${moduleId}/quiz-questions`)).json()).filter(p => p.source === 'editor').length === 1, 'Quelle der Wahrheit: Kartenspeichern lässt Editor-Fragen unberührt');

    // ── Duplizieren, Archivieren, Sortieren
    await click('.editor-card-actions .btn', '⧉'); await sleep(1500);
    const afterDup = await (await api(`/cards/?module_id=${moduleId}`)).json();
    check(afterDup.filter(c => c.term.includes('(Kopie)')).length === 1 && afterDup.find(c => c.term.includes('(Kopie)')).images.length === 1, 'Duplizieren: Kopie mit Bild (Karte „(Kopie)“)');
    await ev(`(() => { const items = [...document.querySelectorAll('.editor-card')]; const it = items.find(x => x.textContent.includes('Zweite Karte')); it.querySelector('[title="Archivieren"]').click(); })()`); await sleep(1200);
    check((await (await api(`/cards/?module_id=${moduleId}&archived=true`)).json()).some(c => c.term === 'Zweite Karte'), 'Archivieren');
    await ev(`(() => { const l = [...document.querySelectorAll('.editor-list-head input')][0]; l.click(); })()`); await sleep(1000);
    await ev(`(() => { const it = [...document.querySelectorAll('.editor-card')].find(x => x.textContent.includes('Zweite Karte')); it.querySelector('[title="Wiederherstellen"]').click(); })()`); await sleep(1200);
    check(!(await (await api(`/cards/?module_id=${moduleId}&archived=true`)).json()).some(c => c.term === 'Zweite Karte'), 'Wiederherstellen');
    const orderBefore = (await (await api(`/cards/?module_id=${moduleId}`)).json()).filter(c => c.category === 'Grundlagen').map(c => c.term);
    await ev(`(() => { const it = [...document.querySelectorAll('.editor-card')].find(x => x.textContent.includes('Erste Karte')); it.querySelector('[title="Nach unten"]').click(); })()`); await sleep(1200);
    const orderAfter = (await (await api(`/cards/?module_id=${moduleId}`)).json()).filter(c => c.category === 'Grundlagen').map(c => c.term);
    check(JSON.stringify(orderBefore) !== JSON.stringify(orderAfter) && orderAfter.indexOf('Erste Karte') > orderBefore.indexOf('Erste Karte'), 'Sortieren: „Erste Karte“ eine Position nach unten');

    // ── Ungespeicherte Änderungen: Wechsel wird abgefragt
    await click('.editor-card-main', 'Zweite Karte'); await sleep(1000);
    await labelInput('Begriff', 'Zweite Karte (geändert)'); await sleep(300);
    dialogs = []; acceptDialogs = false;
    await click('.editor-card-main', 'Erste Karte'); await sleep(800);
    check(dialogs.length === 1 && dialogs[0].includes('ungespeicherte') && (await ev(`document.querySelector('.editor-card-active').textContent.includes('Zweite')`)), 'Ungespeicherte Änderungen: Kartenwechsel fragt nach und bleibt bei „Abbrechen“');
    // Warnung beim Verlassen (Navigation innerhalb der App)
    dialogs = [];
    await click('a', 'Zum Modul'); await sleep(800);
    if (process.env.DEBUG_EDITOR) console.log('  DEBUG nav dialogs=', JSON.stringify(dialogs), 'loc=', await ev('location.href'), 'dirty=', await state());
    check(dialogs.length >= 1 && (await ev('location.pathname.endsWith("/edit")')), 'Ungespeicherte Änderungen: Verlassen der Seite wird abgefragt und bei „Abbrechen“ verhindert');
    check(await ev('typeof window.onbeforeunload === "function"'), 'Browser-Warnung beim Schließen/Neuladen ist aktiv (beforeunload)');
    // Konflikt: Karte wird zwischenzeitlich über die API geändert
    acceptDialogs = true;
    const second = (await (await api(`/cards/?module_id=${moduleId}`)).json()).find(c => c.term === 'Zweite Karte');
    await api('/cards/' + second.id, 'PUT', { module: 'Editor-Test', category: 'Grundlagen', term: 'Zweite Karte', question: 'Zweite Frage?', definition: 'Von anderer Stelle geändert.' });
    await click('.editor-bar .btn-primary', 'Speichern'); await sleep(1200);
    check(await ev(`!!document.querySelector('.alert-error') && document.querySelector('.alert-error').textContent.includes('inzwischen')`), 'Konflikt: gleichzeitige Änderung an anderer Stelle wird erkannt und gemeldet (kein stilles Überschreiben)');
    check((await (await api('/cards/' + second.id)).json()).definition === 'Von anderer Stelle geändert.', 'Konflikt: fremde Änderung bleibt erhalten');
    await shot('05-konflikt');
    await click('.alert-error .btn', 'Neu laden'); await sleep(1200);

    // ── Sicherheit: bösartige Inhalte
    await api('/cards/' + second.id, 'PUT', { module: 'Editor-Test', category: 'Grundlagen', term: 'Zweite Karte', question: 'Zweite Frage?',
      definition: '<script>window.__xss=1</script><img src=x onerror="window.__xss=2">\n\n[Klick](javascript:window.__xss=3) und [ok](https://example.com/?a=1&b=2)\n\n<b onclick="window.__xss=4">x</b>' });
    await send('Page.navigate', { url: `${BASE}/module/${moduleId}/edit?card=${second.id}` });
    await waitFor(`!!document.querySelector('.editor-form')`, 'Karte laden'); await sleep(2000);
    await click('.editor-bar .btn', 'Vorschau'); await sleep(600);
    check(await ev(`(() => { const p = document.querySelector('.editor-preview'); return !!p && !p.querySelector('script, img, [onclick], [onerror]') && !p.querySelector('[href^="javascript:"], [src]') && p.textContent.includes('<script>'); })()`), 'Sicherheit: Skript, Event-Handler und javascript:-Link erscheinen in der Vorschau nur als Text');
    check(await ev(`window.__xss === undefined`), 'Sicherheit: kein eingeschleuster Code wurde ausgeführt');
    check(await ev(`(() => { const a = [...document.querySelectorAll('.editor-preview a')]; return a.length === 1 && a[0].href.startsWith('https://example.com/') && a[0].rel.includes('noopener'); })()`), 'Sicherheit: nur der https-Link wird als Link ausgegeben');
    await send('Page.navigate', { url: `${BASE}/module/${moduleId}` }); await sleep(2500);
    check(await ev(`window.__xss === undefined && !document.querySelector('.keyfact script, .card script')`), 'Sicherheit: auch die Lernansicht führt keinen Code aus');

    // ── Mobil / Dunkel
    await send('Page.navigate', { url: `${BASE}/module/${moduleId}/edit?card=${saved.id}` });
    await waitFor(`!!document.querySelector('.editor-form')`, 'Karte'); await sleep(2000);
    await setViewport(390, 844, true); await sleep(500);
    await shot('06-editor-mobil-hell');
    check(!(await hScroll()), 'Mobil (390 px): kein horizontaler Seitenscroll im Editor');
    check(await ev(`[...document.querySelectorAll('.rte-toolbar .btn, .editor-bar .btn')].every(b => b.getBoundingClientRect().height >= 28 && b.getBoundingClientRect().right <= innerWidth + 1)`), 'Mobil: Bedienelemente der Werkzeugleiste bleiben im sichtbaren Bereich');
    await setTheme('dark'); await sleep(400); await shot('07-editor-mobil-dunkel');
    const dark = await ev(`getComputedStyle(document.querySelector('.rte-surface')).backgroundColor`);
    check(dark !== 'rgb(255, 255, 255)', 'Dunkler Modus: Editorfläche ist dunkel (' + dark + ')');
    await setViewport(1280, 900, false); await sleep(400); await shot('08-editor-desktop-dunkel');
    check(!(await hScroll()), 'Desktop dunkel: kein horizontaler Scroll');
    await setTheme('light');
    check(consoleErrors.filter(e => !/favicon|WebSocket|Failed to load resource/i.test(e)).length === 0, 'Keine JavaScript-Fehler in der Konsole' + (consoleErrors.length ? ' — ' + consoleErrors.slice(0, 3).join(' | ') : ''));
    ws.close();
  } finally {
    try { chrome && chrome.kill(); } catch { }
    try { web.kill(); } catch { }
  }
  const failed = results.filter(r => !r.ok).length;
  console.log(`\n${results.length - failed}/${results.length} Editor-Browserprüfungen bestanden`);
  process.exit(failed ? 1 : 0);
}
main().catch(e => { console.error(e); process.exit(2); });
