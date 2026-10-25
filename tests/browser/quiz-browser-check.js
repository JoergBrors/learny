// Browserprüfung der Quiz-Oberfläche per Chrome DevTools Protocol (nur Node-Bordmittel, keine Pakete)
const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');

const OUT = process.argv[2];
const WEB_DLL = process.argv[3];
const CONTENT_ROOT = process.argv[4];
const PORT = 5188, CDP = 9333;
const BASE = `http://127.0.0.1:${PORT}`;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const results = [];
const check = (ok, name) => { results.push({ ok, name }); console.log((ok ? 'OK   ' : 'FAIL ') + name); };

async function main() {
  fs.mkdirSync(OUT, { recursive: true });
  const dataDir = path.join(OUT, 'data'); fs.mkdirSync(dataDir, { recursive: true });
  const env = { ...process.env, ASPNETCORE_URLS: BASE, ASPNETCORE_ENVIRONMENT: 'Development', AUTH_MODE: 'dev', DB_PROVIDER: 'sqlite', SQLITE_PATH: path.join(dataDir, 'b.db'),
    DATA_PROTECTION_DIR: path.join(dataDir, 'keys'), OPENAI_API_KEY: '', MCP_API_KEY: 'browser-test-key', ASPNETCORE_CONTENTROOT: CONTENT_ROOT };
  env.PATH = env.PATH.split(path.delimiter).filter(p => !/amazon/i.test(p)).join(path.delimiter);
  const web = spawn('dotnet', [WEB_DLL], { cwd: dataDir, env, stdio: 'ignore' });
  let chrome;
  try {
    for (let i = 0; i < 100; i++) { try { if ((await fetch(BASE + '/health')).ok) break; } catch { } await sleep(300); }

    // Daten über die API anlegen
    const login = await fetch(BASE + '/auth/login?devUser=browser', { redirect: 'manual' });
    const cookie = login.headers.getSetCookie().map(c => c.split(';')[0]).join('; ');
    const api = (p, method, body) => fetch(BASE + '/api' + p, { method, headers: { 'Content-Type': 'application/json', Cookie: cookie }, body: body ? JSON.stringify(body) : undefined });
    const cards = [
      { module: 'Browser-Quiz', category: 'Grundlagen', term: 'Single-Karte', question: 'Frage zur Single-Karte?', definition: 'Definition Single mit etwas Text.', slide_number: 1,
        quiz: [{ type: 'single_choice', question: 'Welche Antwort stimmt?', options: ['Alpha', 'Beta', 'Gamma', 'Delta'], correct_index: 1, explanation: 'Beta stimmt.' }] },
      { module: 'Browser-Quiz', category: 'Grundlagen', term: 'Multi-Karte', question: 'Frage zur Multi-Karte?', definition: 'Definition Multi.', slide_number: 2,
        quiz: [{ type: 'multiple_response', question: 'Welche sind Primzahlen?', options: ['2', '3', '4', '6'], correct_indexes: [0, 1], explanation: '2 und 3.' },
               { type: 'free_text', question: 'Erkläre den Begriff Multi in eigenen Worten.', explanation: 'Muster: Multi ist ein Auswahlformat mit mehreren richtigen Antworten.' }] },
    ];
    const ids = [];
    for (const c of cards) {
      const r = await api('/cards/', 'POST', c); const j = await r.json(); ids.push(j.id);
      await api(`/cards/${j.id}/state`, 'PUT', { is_checked: true, marked_review: false });
    }
    const moduleId = (await (await api('/cards/' + ids[0], 'GET')).json()).module_id;

    // Chrome starten
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
      if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') consoleErrors.push(m.params.args.map(a => a.value || a.description).join(' '));
    });
    const send = (method, params = {}) => new Promise(res => { const id = ++msgId; pending.set(id, res); ws.send(JSON.stringify({ id, method, params })); });
    const ev = async (expr) => { const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }); if (r.result.exceptionDetails) throw new Error(r.result.exceptionDetails.exception?.description || 'eval'); return r.result.result.value; };
    const waitFor = async (expr, label, ms = 15000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { try { if (await ev(expr)) return true; } catch { } await sleep(150); } console.log('  (Timeout: ' + label + ')'); return false; };
    const shot = async name => { const r = await send('Page.captureScreenshot', { format: 'png' }); fs.writeFileSync(path.join(OUT, name + '.png'), Buffer.from(r.result.data, 'base64')); };
    const click = async (sel, text) => ev(`(() => { const e = [...document.querySelectorAll(${JSON.stringify(sel)})].find(x => x.offsetParent !== null && x.textContent.trim().includes(${JSON.stringify(text)})); if (!e) return false; e.click(); return true; })()`);
    const setViewport = (w, h, mobile) => send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 1, mobile });
    const setTheme = t => ev(`document.documentElement.setAttribute('data-theme', '${t}')`);
    const hScroll = () => ev('document.documentElement.scrollWidth > document.documentElement.clientWidth + 1');

    await send('Page.enable'); await send('Runtime.enable');
    await setViewport(1280, 900, false);
    await send('Page.navigate', { url: BASE + '/auth/login?devUser=browser' });
    await sleep(1500);
    await send('Page.navigate', { url: `${BASE}/module/${moduleId}` });
    check(await waitFor(`!!document.querySelector('.module-page-head') && [...document.querySelectorAll('button')].some(b => b.textContent.includes('Quiz-Einstellungen'))`, 'Modulseite'), 'Modulseite lädt interaktiv (Desktop)');
    await shot('01-modulseite-desktop-hell');

    // ── Konfiguration
    await sleep(2500);   // Blazor-Schaltkreis aufbauen lassen (Prerender ist sofort sichtbar, aber noch nicht interaktiv)
    let panelOpen = false;
    for (let i = 0; i < 6 && !panelOpen; i++) { await click('button', 'Quiz-Einstellungen'); panelOpen = await waitFor(`!!document.querySelector('#quiz-config-title')`, 'Panel', 2500); }
    check(panelOpen, 'Quiz-Einstellungen öffnen: Konfigurationspanel sichtbar');
    await shot('02-konfig-desktop-hell');
    await setTheme('dark'); await sleep(300); await shot('03-konfig-desktop-dunkel');
    const darkBg = await ev(`getComputedStyle(document.querySelector('.form-grid').closest('section')).backgroundColor`);
    check(darkBg && darkBg !== 'rgb(255, 255, 255)', 'Dunkler Modus: Panel-Hintergrund ist nicht weiß (' + darkBg + ')');
    await setTheme('light');
    await setViewport(390, 844, true); await sleep(400);
    await shot('04-konfig-mobil-hell');
    check(!(await hScroll()), 'Mobil (390 px): kein horizontaler Seitenscroll bei offener Konfiguration');
    await setTheme('dark'); await sleep(200); await shot('05-konfig-mobil-dunkel'); await setTheme('light');
    await setViewport(1280, 900, false); await sleep(300);

    // Multiple Choice aus → Formate deaktiviert und abgeschaltet
    await ev(`(() => { const l = [...document.querySelectorAll('label.check')].find(x => x.textContent.includes('Multiple Choice erlauben')); l.querySelector('input').click(); })()`);
    await sleep(500);
    const states = await ev(`[...document.querySelectorAll('fieldset input')].map(i => ({ t: i.parentElement.textContent.trim().slice(0, 14), d: i.disabled, c: i.checked }))`);
    check(states[0].d && states[1].d && !states[0].c && !states[1].c && !states[2].d && states[2].c, 'Multiple Choice aus → Single Choice/Multiple Response deaktiviert und abgewählt, Freitext bleibt');
    await ev(`(() => { const l = [...document.querySelectorAll('label.check')].find(x => x.textContent.includes('Multiple Choice erlauben')); l.querySelector('input').click(); })()`);
    await sleep(500);
    // Fehlerzustand: Bestehensgrenze 0
    await ev(`(() => { const i = [...document.querySelectorAll('.form-grid label')].find(x => x.textContent.includes('Bestehensgrenze')).querySelector('input'); i.value = '0'; i.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    await sleep(300);
    await click('button', 'Speichern'); await sleep(800);
    check(await ev(`!!document.querySelector('.alert-error') && document.querySelector('.alert-error').textContent.includes('pass_percent')`), 'Fehlerzustand: ungültige Bestehensgrenze → Fehlermeldung wird angezeigt');
    await shot('06-konfig-fehler');
    await ev(`(() => { const i = [...document.querySelectorAll('.form-grid label')].find(x => x.textContent.includes('Bestehensgrenze')).querySelector('input'); i.value = '50'; i.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    await sleep(300);
    await click('button', 'Speichern'); await sleep(800);
    check(await ev(`!!document.querySelector('.alert-ok')`), 'Konfiguration speichern → Bestätigung');
    await click('button', 'Quiz-Einstellungen'); await sleep(300);   // Panel schließen (Toggle)

    // ── Modulquiz
    const modal = `document.querySelector('.modal')`;
    async function answerAll(prefix) {
      const types = [];
      for (let guard = 0; guard < 10; guard++) {
        if (await ev(`!!${modal} && !!${modal}.querySelector('.card-quiz-result')`)) break;
        const kind = await ev(`(() => { const m = ${modal}; if (!m) return ''; if (m.querySelector('textarea')) return 'free'; const p = m.querySelector('.quiz-progress'); return p ? p.textContent : ''; })()`);
        const q = await ev(`${modal}.querySelector('.quiz-question').textContent`);
        types.push(q.slice(0, 30));
        if (kind === 'free') {
          await ev(`(() => { const t = ${modal}.querySelector('textarea'); t.value = 'Multi ist ein Auswahlformat mit mehreren richtigen Antworten'; t.dispatchEvent(new Event('input', { bubbles: true })); })()`);
        } else if (q.includes('Primzahlen')) {
          await ev(`(() => { const o = ${modal}.querySelectorAll('.quiz-option'); o[0].click(); })()`); await sleep(200);
          await ev(`(() => { const o = ${modal}.querySelectorAll('.quiz-option'); o[1].click(); })()`);
        } else {
          await ev(`(() => { const o = ${modal}.querySelectorAll('.quiz-option'); o[1].click(); })()`);
        }
        await sleep(300);
        await shot(prefix + '-frage-' + types.length);
        if (!(await click('.modal .btn', 'Weiter'))) { await click('.modal .btn', 'Abgeben'); await sleep(1200); break; }
        await sleep(300);
      }
      return types;
    }
    check(await click('button', 'Quiz starten'), 'Modulquiz: „Quiz starten“ (Modulseite)');
    check(await waitFor(`!!${modal}`, 'Dialog'), 'Modulquiz-Dialog öffnet');
    await shot('07-modulquiz-start');
    check(await click('.modal .btn', 'Quiz starten'), 'Modulquiz: Quiz im Dialog starten');
    check(await waitFor(`!!${modal} && !!${modal}.querySelector('.quiz-question')`, 'Frage'), 'Erste Frage erscheint');
    const asked = await answerAll('08-modul');
    check(await waitFor(`!!${modal}.querySelector('.card-quiz-result')`, 'Ergebnis', 20000), 'Modulquiz: Ergebnis wird angezeigt (Single, Multiple Response, Freitext absolviert: ' + asked.length + ' Fragen)');
    await shot('09-modulquiz-ergebnis');
    const resultText = await ev(`${modal}.querySelector('.card-quiz-result').textContent`);
    check(/Bestanden|Nicht bestanden/.test(resultText) && /Punkte/.test(resultText), 'Ergebnis nennt Punkte und Bestanden-Status');
    check(await ev(`${modal}.querySelectorAll('.quiz-option-correct').length > 0`), 'Auswahlfragen zeigen nach der Abgabe die richtigen Optionen');
    await setTheme('dark'); await sleep(300); await shot('10-modulquiz-ergebnis-dunkel'); await setTheme('light');
    await setViewport(390, 844, true); await sleep(300); await shot('11-modulquiz-ergebnis-mobil');
    check(!(await hScroll()), 'Mobil: Ergebnisansicht ohne horizontalen Scroll');
    await setViewport(1280, 900, false);

    // Wiederholen
    check(await click('.modal .btn', 'Neues Quiz'), 'Wiederholen: „Neues Quiz“');
    check(await waitFor(`!!${modal} && [...${modal}.querySelectorAll('.btn')].some(b => b.textContent.includes('Quiz starten'))`, 'Config'), 'Nach „Neues Quiz“ erscheint wieder die Startansicht');
    await click('.modal .btn', 'Quiz starten');
    check(await waitFor(`!!${modal}.querySelector('.quiz-question')`, 'zweiter Lauf'), 'Wiederholung: zweiter Quizlauf startet');
    await click('.modal .icon-btn', '✕'); await sleep(500);

    // ── Kartenquiz (Folienmodus: "Quiz für diese Seite")
    check(await click('button', 'Folienmodus'), 'Folienmodus aktivieren');
    await sleep(800);
    check(await click('button', 'Quiz für diese Seite'), 'Kartenquiz: „Quiz für diese Seite“');
    check(await waitFor(`!!${modal} && !!${modal}.querySelector('.quiz-question')`, 'Kartenquiz'), 'Kartenquiz zeigt eine Frage');
    const cardTypes = await answerAll('12-karte');
    check(await waitFor(`!!${modal}.querySelector('.card-quiz-result')`, 'Kartenergebnis', 20000), 'Kartenquiz: Ergebnis angezeigt (' + cardTypes.length + ' Frage(n))');
    await shot('13-kartenquiz-ergebnis');
    await click('.modal .btn', 'Fertig'); await sleep(400);

    // ── Fehlerzustand: Quiz deaktiviert
    await click('button', 'Quiz-Einstellungen'); await sleep(500);
    await ev(`(() => { const l = [...document.querySelectorAll('label.check')].find(x => x.textContent.includes('Quiz aktivieren')); l.querySelector('input').click(); })()`);
    await sleep(300); await click('button', 'Speichern'); await sleep(800);
    check(await ev(`!!document.querySelector('.alert-ok')`), 'Quiz deaktivieren und speichern');
    await click('button', 'Quiz-Einstellungen'); await sleep(300);
    await click('button', 'Karten-Quiz'); await sleep(1200);
    await shot('14-quiz-deaktiviert');
    const disabledMsg = await ev(`(() => { const m = ${modal}; return m ? m.textContent : ''; })()`);
    check(disabledMsg.includes('deaktiviert'), 'Fehlerzustand: deaktiviertes Quiz zeigt eine klare Meldung (' + disabledMsg.replace(/\s+/g, ' ').slice(0, 90) + ')');
    check(consoleErrors.filter(e => !/favicon|WebSocket|Failed to load resource/i.test(e)).length === 0, 'Keine JavaScript-Fehler in der Konsole' + (consoleErrors.length ? ' — ' + consoleErrors.slice(0, 3).join(' | ') : ''));
    ws.close();
  } finally {
    try { chrome && chrome.kill(); } catch { }
    try { web.kill(); } catch { }
  }
  const failed = results.filter(r => !r.ok).length;
  console.log(`\n${results.length - failed}/${results.length} Browserprüfungen bestanden`);
  process.exit(failed ? 1 : 0);
}
main().catch(e => { console.error(e); process.exit(2); });
