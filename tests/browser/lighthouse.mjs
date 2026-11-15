// Lighthouse gegen eine angemeldete Dev-Instanz (Chrome ohne Erweiterungen, Anmeldung per Dev-Login im selben Chrome, damit auch der WebSocket authentifiziert ist —
// Cookie-Header per --extra-headers erreichen den WebSocket nicht und lassen den Circuit mit UnauthorizedAccessException enden).
// Voraussetzung: Lighthouse-CLI (npm install lighthouse in einem Ordner außerhalb des Repositories), LIGHTHOUSE_CLI=<Pfad>/node_modules/lighthouse/cli/index.js
// Start: LEARNY_BROWSER_TEST=1 LIGHTHOUSE_CLI=... LH_OUT=<Ordner> [LH_RUNS=5] [LH_PATH=/dashboard] node tests/browser/lighthouse.mjs
import { spawnSync } from 'node:child_process';
import { mkdirSync, readdirSync, readFileSync } from 'node:fs';
import { A, newPage, sleep, finish } from './lib.mjs';
const cli = process.env.LIGHTHOUSE_CLI, out = process.env.LH_OUT, runs = Number(process.env.LH_RUNS ?? 5), path = process.env.LH_PATH ?? '/dashboard';
if (!cli || !out) { console.error('LIGHTHOUSE_CLI und LH_OUT setzen.'); process.exit(2); }
mkdirSync(out, { recursive: true });
const a = await newPage(`${A}/auth/login?devUser=lh-user`); await sleep(2500);
await a.go(A + path); await sleep(1500);
for (const prof of ['mobile', 'desktop']) for (let i = 1; i <= runs; i++) {
  const args = [cli, A + path, '--port=' + (process.env.LEARNY_CDP_PORT ?? 9333), '--disable-storage-reset', '--output=json', `--output-path=${out}/${prof}-${i}.json`, '--quiet', '--only-categories=performance,accessibility,best-practices,seo'];
  if (prof === 'desktop') args.push('--preset=desktop');
  const r = spawnSync('node', args, { encoding: 'utf8' }); if (r.status !== 0) console.log('Lighthouse-Fehler', prof, i, (r.stderr || '').slice(0, 200));
}
const med = v => [...v].sort((x, y) => x - y)[Math.floor(v.length / 2)];
for (const prof of ['mobile', 'desktop']) {
  const rs = readdirSync(out).filter(f => f.startsWith(prof) && f.endsWith('.json')).map(f => JSON.parse(readFileSync(`${out}/${f}`, 'utf8')));
  if (!rs.length) continue; const g = f => rs.map(f), au = id => g(r => r.audits[id]?.numericValue ?? 0);
  const row = (n, v, d = 0) => console.log(`  ${n.padEnd(15)} Median ${med(v).toFixed(d).padStart(7)}  (min ${Math.min(...v).toFixed(d)}, max ${Math.max(...v).toFixed(d)})`);
  console.log(`${prof}: ${rs.length} Läufe, ${path}`);
  row('Performance', g(r => r.categories.performance.score * 100)); row('Accessibility', g(r => r.categories.accessibility.score * 100)); row('Best Practices', g(r => r.categories['best-practices'].score * 100)); row('SEO', g(r => r.categories.seo.score * 100));
  row('FCP ms', au('first-contentful-paint')); row('LCP ms', au('largest-contentful-paint')); row('TBT ms', au('total-blocking-time')); row('CLS', au('cumulative-layout-shift'), 3); row('lange Tasks', g(r => r.audits['long-tasks']?.details?.items?.length ?? 0));
  console.log('  Auffällig: ' + Object.values(rs[0].audits).filter(x => x.score !== null && x.score < 0.9 && !['informative', 'notApplicable', 'manual'].includes(x.scoreDisplayMode)).map(x => `${x.id}=${x.score}`).join(', '));
}
await finish(0);
