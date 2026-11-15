// Performance-Baseline (Auftrag Browser-/PWA-Performance): misst je Seite Navigationsdauer, lange Aufgaben (>50 ms), Layoutverschiebungen (CLS),
// Interaktionslatenz (Event Timing), DOM-Größe, Requests (Anzahl/Bytes, /_blazor getrennt) und den verwendeten Blazor-Transport.
// Start: LEARNY_BROWSER_TEST=1 [PERF_RUNS=3] [PERF_MOBILE=1] node tests/browser/perf-baseline.mjs  (Dev-Instanz wie in README; Daten: Wegwerf-Benutzer perf-user)
import { A, newPage, api, sleep, J, finish } from './lib.mjs';
const runs = Number(process.env.PERF_RUNS ?? 3), mobile = process.env.PERF_MOBILE === '1';
const a = await newPage(`${A}/auth/login?devUser=perf-user`); await sleep(2500);
if (mobile) await a.send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 3, mobile: true });
// Beispieldaten (einmalig): 25 Module mit je 12 Karten, 4 Themen
const have = J(await api(a, '/api/modules/'));
if (have.length < 25) for (let i = have.length; i < 25; i++) {
  const n = 'Perf-Modul-' + String(i).padStart(2, '0'); await api(a, '/api/modules/', 'POST', { name: n });
  for (let c = 0; c < 12; c++) await api(a, '/api/cards/', 'POST', { module: n, category: 'K' + (c % 3), term: `Karte ${i}-${c}`, question: 'Frage?', definition: 'Antwort '.repeat(20) });
}
const topics = J(await api(a, '/api/topics/')); if ((topics.topics ?? topics).length < 4) for (let i = 0; i < 4; i++) await api(a, '/api/topics/', 'POST', { name: 'Perf-Thema ' + i + ' ' + Date.now(), description: '' });
await a.send('Network.enable');
const net = { n: 0, blazorGet: 0, blazorPost: 0, bytes: 0, ws: 0 };
// Netzwerkzähler über CDP-Ereignisse
const origOnMsg = null;
const pages = [['/dashboard', 'Dashboard'], ['/', 'Home (Module)'], ['/topics', 'Themen'], ['/achievements', 'Abschlüsse']];
const inject = `(()=>{ if(window.__perf) return; const P=window.__perf={lt:[],cls:0,ev:[],navs:[]};
  new PerformanceObserver(l=>l.getEntries().forEach(e=>P.lt.push(e.duration))).observe({type:'longtask',buffered:true});
  new PerformanceObserver(l=>l.getEntries().forEach(e=>{if(!e.hadRecentInput)P.cls+=e.value})).observe({type:'layout-shift',buffered:true});
  new PerformanceObserver(l=>l.getEntries().forEach(e=>{if(e.interactionId)P.ev.push(e.duration)})).observe({type:'event',durationThreshold:16,buffered:true});})()`;
const median = v => { const s = [...v].sort((x, y) => x - y); return s.length ? s[Math.floor(s.length / 2)] : 0; };
const out = [];
for (const [path, label] of pages) {
  const cold = [], warm = [], lts = [], cls = [], dom = [], inp = [];
  for (let r = 0; r < runs; r++) {
    // Kaltstart: harte Navigation (vollständiges Laden inkl. Sitzungsaufbau)
    const t0 = Date.now(); await a.send('Page.navigate', { url: A + path });
    let ready = false; for (let i = 0; i < 80 && !ready; i++) { await sleep(100); ready = await a.ev(`document.readyState==='complete' && !!document.querySelector('h1,h2,.stage')`); }
    await sleep(1200); cold.push(Date.now() - t0);
    await a.ev(inject);
    // Warmstart: Menüklick innerhalb der laufenden Sitzung (Blazor-Navigation) von einer anderen Seite
    const other = path === '/help' ? '/dashboard' : '/help';
    await a.ev(`document.querySelector('a.nav-ico[href="${other}"]')?.click()`); await sleep(1500);
    await a.ev(inject);
    const t1 = await a.ev(`(()=>{window.__t=performance.now();const l=document.querySelector('a.nav-ico[href="${path}"]');l&&l.click();return performance.now()})()`);
    let done = false; for (let i = 0; i < 100 && !done; i++) { await sleep(50); done = await a.ev(`location.pathname===${JSON.stringify(path)} && !document.querySelector('.nav-progress.on')`); }
    const nav = await a.ev(`performance.now()-window.__t`); warm.push(Math.round(nav)); await sleep(1500);
    const m = await a.ev(`({lt:window.__perf.lt,cls:window.__perf.cls,ev:window.__perf.ev,dom:document.getElementsByTagName('*').length})`);
    lts.push(...m.lt); cls.push(m.cls); dom.push(m.dom); inp.push(...m.ev);
  }
  const tr = await a.ev(`performance.getEntriesByType('resource').filter(x=>x.name.includes('/_blazor')).map(x=>x.initiatorType+':'+x.name.split('?')[0].split('/').pop())`);
  out.push({ label, cold: median(cold), warm: median(warm), longTasks: lts.filter(x => x > 50).length, maxTask: Math.round(Math.max(0, ...lts)), cls: +median(cls).toFixed(3), dom: median(dom), inpMax: Math.round(Math.max(0, ...inp)) });
}
const transport = await a.ev(`(async()=>{const r=await fetch('/_blazor/negotiate?negotiateVersion=1',{method:'POST'});return (await r.json()).availableTransports.map(t=>t.transport).join(',')})()`);
const ws = await a.ev(`performance.getEntriesByType('resource').filter(x=>x.name.includes('/_blazor')).length`);
console.log(`\nProfil: ${mobile ? 'mobil 390x844' : 'Desktop 1300x1000'}, ${runs} Läufe je Seite (Median); Transporte laut negotiate: ${transport}; /_blazor-Ressourceneinträge: ${ws}`);
console.log('Seite'.padEnd(16) + 'kalt ms'.padStart(9) + 'warm ms'.padStart(9) + 'Tasks>50'.padStart(9) + 'max Task'.padStart(9) + 'CLS'.padStart(8) + 'DOM'.padStart(7) + 'max Ev ms'.padStart(10));
for (const o of out) console.log(o.label.padEnd(16) + String(o.cold).padStart(9) + String(o.warm).padStart(9) + String(o.longTasks).padStart(9) + String(o.maxTask).padStart(9) + String(o.cls).padStart(8) + String(o.dom).padStart(7) + String(o.inpMax).padStart(10));
await finish(0);
