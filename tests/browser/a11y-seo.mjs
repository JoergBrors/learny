// Barrierefreiheit/SEO-Korrekturen aus dem Lighthouse-Bericht: Meta-Beschreibung, sichtbarer Text im zugänglichen Namen (Sidebar, Kartenbuttons), Kontrast der geänderten Farben (WCAG AA 4,5:1) in Hell und Dunkel.
// Start: LEARNY_BROWSER_TEST=1 node tests/browser/a11y-seo.mjs
import { A, newPage, api, ok, sleep, J, finish } from './lib.mjs';
try {
  const a = await newPage(`${A}/auth/login?devUser=a11y-user`); await sleep(2500);
  const n = 'A11y-' + Date.now(); await api(a, '/api/modules/', 'POST', { name: n });
  await api(a, '/api/cards/', 'POST', { module: n, category: 'K', term: 'Karte', question: 'F?', definition: 'A' });
  const m = J(await api(a, '/api/modules/')).find(x => x.name === n).id;
  const lum = s => { const [r, g, b] = (s.match(/[\d.]+/g) ?? []).slice(0, 3).map(Number).map(v => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; }); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
  const ratio = (fg, bg) => { const x = lum(fg), y = lum(bg); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };
  await a.go(`${A}/dashboard`); await sleep(800);
  ok(await a.ev(`document.querySelector('meta[name=description]')?.content.length>30`), 'Meta-Beschreibung vorhanden (statisch, ohne private Inhalte)');
  ok(await a.ev(`!/Learny: Lernkarten/.test(document.title)&&document.querySelector('meta[name=description]').content.includes('Lernkarten')`), 'Meta-Beschreibung enthält nur allgemeinen Text');
  const bad = await a.ev(`[...document.querySelectorAll('.nav-ico')].filter(e=>{const t=(e.querySelector('.nav-label')?.textContent||'').trim().toLowerCase();const l=(e.getAttribute('aria-label')||'').toLowerCase();return t&&!l.includes(t)}).map(e=>e.getAttribute('aria-label'))`);
  ok(bad.length === 0, `Sidebar: sichtbarer Text steckt im zugänglichen Namen (Abweichungen: ${bad.join(', ') || 'keine'})`);
  const th = await a.ev(`[...document.querySelectorAll('button[data-shell="theme"]')].filter(b=>b.offsetParent!==null).map(b=>({l:(b.getAttribute('aria-label')||'').toLowerCase(),t:b.innerText.replace(/[^\p{L}]/gu,'').toLowerCase()}))`);
  ok(th.length > 0 && th.every(x => !x.t || x.l.includes(x.t)), `Darstellungs-Schaltfläche: sichtbarer Text im zugänglichen Namen (${JSON.stringify(th)})`);
  for (const theme of ['light', 'dark']) {
    await a.ev(`localStorage.setItem('lc-theme','${theme}')`); await a.go(`${A}/module/${m}`); await sleep(1200);
    const raw = await a.ev(`(()=>{const out={};const pick=(sel,name)=>{const e=document.querySelector(sel);if(!e)return;const s=getComputedStyle(e);out[name]=[s.color,s.backgroundColor==='rgba(0, 0, 0, 0)'?getComputedStyle(document.body).backgroundColor:s.backgroundColor]};
      pick('.btn-primary','btn-primary');pick('.btn-known','btn-known');pick('.btn-review','btn-review');pick('.stat-known','stat-known');pick('.stat-review','stat-review');return out})()`);
    const r = Object.fromEntries(Object.entries(raw ?? {}).map(([k, [fg, bg]]) => [k, +ratio(fg, bg).toFixed(2)]));
    // gedrückte Zustände (von Lighthouse nicht erfasst): Klasse setzen und Kontrast messen
    const act = await a.ev(`(()=>{const out={};for(const [sel,cls,name] of [['.btn-known','btn-known-active','known-aktiv'],['.btn-review','btn-review-active','review-aktiv']]){const e=document.querySelector(sel);if(!e)continue;e.style.transition='none';e.classList.add(cls);const s=getComputedStyle(e);out[name]=[s.color,s.backgroundColor];e.classList.remove(cls);e.style.transition=''}return out})()`);
    const ra = Object.fromEntries(Object.entries(act ?? {}).map(([k, [fg, bg]]) => [k, +ratio(fg, bg).toFixed(2)]));
    ok(Object.keys(ra).length === 2 && Object.values(ra).every(v => v >= 4.5), `Kontrast gedrückte Zustände ${theme}: ${JSON.stringify(ra)} (alle >= 4,5)`);
    ok(Object.keys(r).length >= 3 && Object.values(r).every(v => v >= 4.5), `Kontrast ${theme}: ${JSON.stringify(r)} (alle >= 4,5)`);
    const labels = await a.ev(`[...document.querySelectorAll('.flip-scene ~ * button, button.btn-touch, button.btn-known, button.btn-review')].filter(e=>e.getAttribute('aria-label')).filter(e=>{const t=e.innerText.replace(/[^\p{L}\p{N} ]/gu,'').trim().toLowerCase();return t&&!e.getAttribute('aria-label').toLowerCase().includes(t)}).map(e=>e.getAttribute('aria-label'))`);
    ok(labels.length === 0, `Kartenbuttons (${theme}): sichtbarer Text im zugänglichen Namen (Abweichungen: ${labels.join(' | ') || 'keine'})`);
  }
  const errs = a.errors; ok(errs.length === 0, `Keine JavaScript-Fehler (${errs.length}) ${errs.slice(0, 2).join(' | ')}`);
} catch (e) { console.log('SKRIPTFEHLER', e); await finish(1); }
await finish();
