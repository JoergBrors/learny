// Server-Phasen der SSR-Seiten (nur Development): liest den Server-Timing-Header (total, db, Anzahl Abfragen) je Seite; mindestens 5 Durchläufe, Median/Ausreißer.
// Start: LEARNY_BROWSER_TEST=1 [PERF_RUNS=5] [PERF_TOPICS=12] node tests/browser/perf-server.mjs   (Dev-Instanz mit AUTH_MODE=dev, siehe README)
import { A, newPage, api, sleep, J, finish } from './lib.mjs';
const runs = Number(process.env.PERF_RUNS ?? 5), nTopics = Number(process.env.PERF_TOPICS ?? 12);
const a = await newPage(`${A}/auth/login?devUser=perf-user`); await sleep(2500);
// Daten: nTopics Themen mit je 4 veröffentlichten Modulen (je 6 Karten) — Größenordnung eines aktiven Autors
const topicsNow = J(await api(a, '/api/topics/')); const own = (topicsNow.own ?? topicsNow.topics ?? []);
if (own.length < nTopics) {
  const mods = J(await api(a, '/api/modules/')).filter(m => m.name.startsWith('PS-'));
  for (let i = mods.length; i < nTopics * 4; i++) {
    const n = 'PS-' + String(i).padStart(3, '0'); const m = J(await api(a, '/api/modules/', 'POST', { name: n })).id;
    for (let c = 0; c < 6; c++) await api(a, '/api/cards/', 'POST', { module: n, category: 'K', term: `K${i}-${c}`, question: 'F?', definition: 'A ' + c });
    mods.push({ id: m, name: n });
  }
  for (let t = own.length; t < nTopics; t++) {
    const id = J(await api(a, '/api/topics/', 'POST', { name: `PS-Thema ${t} ${Date.now()}`, description: '' })).id;
    for (let k = 0; k < 4; k++) await api(a, `/api/topics/${id}/modules`, 'POST', { module_id: mods[t * 4 + k].id });
    await api(a, `/api/topics/${id}/publish`, 'POST', { note: 'v1' });
  }
}
const pages = ['/dashboard', '/topics', '/', '/achievements'];
const med = v => [...v].sort((x, y) => x - y)[Math.floor(v.length / 2)];
console.log(`Themen: ${J(await api(a, '/api/topics/')).own?.length ?? '?'}; ${runs} Durchläufe je Seite`);
console.log('Seite'.padEnd(14) + 'total ms (Median / min–max)'.padEnd(34) + 'db ms (Median)'.padEnd(16) + 'Abfragen');
for (const p of pages) {
  const tot = [], db = [], q = [];
  for (let r = 0; r < runs + 1; r++) {   // erster Lauf = Aufwärmen, nicht gewertet
    const h = await a.ev(`fetch(${JSON.stringify(p)},{credentials:'same-origin'}).then(async r=>{await r.text();return r.headers.get('server-timing')})`);
    if (r === 0 || !h) continue;
    const m = /total;dur=([\d.]+).*db;dur=([\d.]+);desc="(\d+) queries"/.exec(h); if (!m) continue;
    tot.push(+m[1]); db.push(+m[2]); q.push(+m[3]);
  }
  console.log(p.padEnd(14) + `${med(tot).toFixed(0)} (${Math.min(...tot).toFixed(0)}–${Math.max(...tot).toFixed(0)})`.padEnd(34) + med(db).toFixed(0).padEnd(16) + med(q));
}
await finish(0);
