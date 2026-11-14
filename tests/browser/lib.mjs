// Gemeinsame Hilfen für die optionalen Browserprüfungen (ADR 0052/0053). Keine Abhängigkeiten außer Node.js >= 22 (globales WebSocket) und einem installierten Chrome.
// NICHT Teil der regulären Integrationstestsuite. Siehe tests/browser/README.md und docs/testing.md.
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

if (process.env.LEARNY_BROWSER_TEST !== '1') {
  console.error('Abbruch: Diese Prüfung legt Benutzer, Module und Themen an. Nur gegen eine lokale Entwicklungsinstanz mit Wegwerf-Datenbank (AUTH_MODE=dev) ausführen und LEARNY_BROWSER_TEST=1 setzen.');
  process.exit(2);
}

/** Zwei Hostnamen derselben Instanz: Cookies sind je Host getrennt, daher zwei unabhängige Sitzungen (Eigentümer / Co-Autor). */
export const A = process.env.LEARNY_URL_A ?? 'http://localhost:5199';
export const B = process.env.LEARNY_URL_B ?? 'http://127.0.0.1:5199';
const PORT = Number(process.env.LEARNY_CDP_PORT ?? 9333);

function findChrome() {
  const candidates = [process.env.CHROME_PATH, 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', 'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
    '/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/chromium-browser', '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'];
  const found = candidates.find(c => c && existsSync(c));
  if (!found) { console.error('Chrome nicht gefunden. CHROME_PATH setzen.'); process.exit(2); }
  return found;
}

const prof = mkdtempSync(join(tmpdir(), 'learny-cdp-'));
const chrome = spawn(findChrome(), ['--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${prof}`, '--no-first-run', '--window-size=1300,1000', 'about:blank'], { stdio: 'ignore' });

export const sleep = ms => new Promise(r => setTimeout(r, ms));
const results = [];
export const ok = (c, m) => { results.push(c); console.log((c ? '  OK   ' : '  FAIL ') + m); };
export const J = r => JSON.parse(r.j);

/** Öffnet einen Tab; liefert Hilfen zum Navigieren, Auswerten, Klicken und Tippen sowie gesammelte JavaScript-Fehler. */
export async function newPage(url) {
  let t;
  for (let i = 0; i < 40; i++) { try { t = await (await fetch(`http://127.0.0.1:${PORT}/json/new?${encodeURIComponent(url)}`, { method: 'PUT' })).json(); break; } catch { await sleep(300); } }
  if (!t) throw new Error('Chrome-DevTools nicht erreichbar');
  const ws = new WebSocket(t.webSocketDebuggerUrl); await new Promise(r => ws.onopen = r);
  let id = 0; const pend = new Map(); const errors = [];
  ws.onmessage = e => {
    const m = JSON.parse(e.data);
    if (m.id && pend.has(m.id)) { pend.get(m.id)(m); pend.delete(m.id); }
    if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.text + ' ' + (m.params.exceptionDetails.exception?.description ?? ''));
    if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') errors.push('console.error ' + m.params.args.map(a => a.value ?? a.description).join(' '));
  };
  const send = (method, params = {}) => new Promise(r => { const i = ++id; pend.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
  await send('Runtime.enable'); await send('Page.enable');
  const p = {
    errors, send,
    async go(u) { await send('Page.navigate', { url: u }); await sleep(1800); },
    async ev(expr) { const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }); return r.result?.result?.value; },
    async text() { return await p.ev('document.body.innerText'); },
    async waitText(s, ms = 5000) { const t0 = Date.now(); while (Date.now() - t0 < ms) { if ((await p.text())?.includes(s)) return true; await sleep(250); } return false; },
    async click(label, scope = 'button,a,[role=tab]') { return await p.ev(`(()=>{const e=[...document.querySelectorAll(${JSON.stringify(scope)})].find(x=>x.innerText&&x.innerText.trim().startsWith(${JSON.stringify(label)})&&!x.disabled);if(!e)return false;e.click();return true})()`); },
    /** Wie click, aber der sichtbare Text muss das Stichwort nur enthalten (z. B. Quizoptionen „A\nRichtig“). */
    async clickContains(label, scope) { return await p.ev(`(()=>{const e=[...document.querySelectorAll(${JSON.stringify(scope)})].find(x=>x.innerText&&x.innerText.includes(${JSON.stringify(label)})&&!x.disabled);if(!e)return false;e.click();return true})()`); },
    async type(sel, val) {
      await p.ev(`(()=>{const e=document.querySelector(${JSON.stringify(sel)});e.focus();const proto=e.tagName==='TEXTAREA'?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;Object.getOwnPropertyDescriptor(proto,'value').set.call(e,${JSON.stringify(val)});e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}));e.dispatchEvent(new KeyboardEvent('keyup',{bubbles:true}))})()`);
    },
    has: async label => await p.ev(`[...document.querySelectorAll('button,a')].some(x=>x.innerText&&x.innerText.includes(${JSON.stringify(label)}))`),
  };
  return p;
}

/** Same-Origin-API-Aufruf mit der Cookie-Sitzung der Seite. */
export const api = (p, path, method = 'GET', body) => p.ev(`fetch(${JSON.stringify(path)},{method:${JSON.stringify(method)},headers:{'Content-Type':'application/json'},body:${body ? JSON.stringify(JSON.stringify(body)) : 'undefined'}}).then(async r=>({s:r.status,j:await r.text()}))`);

/** Dev-Login als <name> (nur AUTH_MODE=dev); bricht ab, wenn keine Sitzung entsteht (z. B. Produktivbetrieb mit OIDC). */
export async function loginAs(base, name) {
  const p = await newPage(`${base}/auth/login?devUser=${name}`); await sleep(2500);
  const me = await api(p, '/api/me');
  if (me.s !== 200) { console.error(`Abbruch: Dev-Login für ${name} an ${base} fehlgeschlagen (Status ${me.s}). Läuft die Instanz mit AUTH_MODE=dev?`); await finish(2); }
  return { page: p, me: J(me) };
}

export async function finish(code) {
  chrome.kill();
  try { rmSync(prof, { recursive: true, force: true }); } catch { /* Chrome gibt das Profil ggf. verzögert frei */ }
  const okCount = results.filter(Boolean).length;
  console.log(`\n${okCount}/${results.length} Browserprüfungen bestanden`);
  process.exit(code ?? (okCount === results.length && results.length > 0 ? 0 : 1));
}
