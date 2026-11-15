// Ressourcen und Cache-Verhalten öffentlicher Seiten (ohne Anmeldung): Erstbesuch (leerer Cache) gegen Wiederholungsbesuch (neue Navigation und Reload), je Seite mehrere Zyklen.
// Gemessen wird per Resource Timing: Requests, übertragene Bytes (transferSize, 0 = Cache), Protokoll. Standardziel ist die Produktion (nur öffentliche Seiten: es wird nichts angemeldet, nichts geschrieben).
// Start: LEARNY_BROWSER_TEST=1 [PERF_BASE=https://learny.brors.date] [PERF_PATHS=/login,/verify] [PERF_RUNS=5] node tests/browser/perf-cache.mjs
// Hinweis: Die Development-Instanz liefert Dateien ohne langes Caching aus und ist für Cache-Messungen nicht aussagekräftig.
import { newPage, sleep, finish } from './lib.mjs';
const base = (process.env.PERF_BASE ?? 'https://learny.brors.date').replace(/\/$/, ''), runs = Number(process.env.PERF_RUNS ?? 5);
const paths = (process.env.PERF_PATHS ?? '/login,/verify').split(',').map(s => s.trim()).filter(Boolean);
let failed = false;
const a = await newPage('about:blank');
const collect = `JSON.stringify({r:performance.getEntriesByType('resource').map(e=>[e.name,e.transferSize,e.decodedBodySize,e.nextHopProtocol]),nav:(n=>[n.transferSize,Math.round(n.responseStart),n.nextHopProtocol])(performance.getEntriesByType('navigation')[0])})`;
const med = v => [...v].sort((x, y) => x - y)[Math.floor(v.length / 2)];
const kb = v => (v / 1024).toFixed(1);
async function visit(url, reload) {
  if (reload) await a.send('Page.reload'); else await a.go(url);
  await sleep(3500);
  const raw = await a.ev(collect);
  if (typeof raw !== 'string') throw new Error(`Keine Messdaten von ${url} (Seite nicht geladen?)`);
  const d = JSON.parse(raw);
  if (!d.nav || d.r.length === 0) throw new Error(`Keine Ressourcen-/Navigationseinträge für ${url}`);
  const bytes = d.r.reduce((s, x) => s + x[1], 0) + d.nav[0];
  return { requests: d.r.length + 1, bytes, cached: d.r.filter(x => x[1] === 0 && x[2] > 0).length, total: d.r.length, ttfb: d.nav[1], proto: d.nav[2], list: d.r };
}
console.log(`Ziel ${base}, ${runs} Zyklen je Seite (Median, min–max). Requests = Dokument + alle Resource-Timing-Einträge, EINSCHLIESSLICH aus dem Cache bedienter; „aus Cache“ = Einträge mit 0 übertragenen Bytes.`);
try {
  for (const p of paths) {
    const res = { cold: [], warm: [], reload: [] };
    for (let i = 0; i < runs; i++) {
      await a.send('Network.enable'); await a.send('Network.clearBrowserCache');
      res.cold.push(await visit(base + p, false)); res.warm.push(await visit(base + p, false)); res.reload.push(await visit(base + p, true));
    }
    for (const [label, k] of [['Erstbesuch', 'cold'], ['Wiederholung', 'warm'], ['Reload', 'reload']]) {
      const r = res[k];
      console.log(`${p.padEnd(9)} ${label.padEnd(13)} Requests ${med(r.map(x => x.requests))}, übertragen ${kb(med(r.map(x => x.bytes)))} KB (${kb(Math.min(...r.map(x => x.bytes)))}–${kb(Math.max(...r.map(x => x.bytes)))}), aus Cache ${med(r.map(x => x.cached))}/${med(r.map(x => x.total))}, TTFB ${med(r.map(x => x.ttfb))} ms, ${r[0].proto}`);
    }
    console.log('   größte Ressourcen (Erstbesuch): ' + [...res.cold[0].list].sort((x, y) => y[1] - x[1]).slice(0, 4).map(x => `${x[0].replace(base, '').slice(0, 30)} ${kb(x[1])} KB`).join(', '));
  }
} catch (e) { failed = true; console.error('FEHLER:', e.message); }
await finish(failed ? 1 : 0);
