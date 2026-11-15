// PWA: Navigationsbalken, Notausgang und Herunterziehen (pwa.js). Start: LEARNY_BROWSER_TEST=1 node tests/browser/pwa-nav.mjs
import { A, newPage, ok, sleep, finish } from './lib.mjs';
try {
  const a = await newPage(`${A}/auth/login?devUser=pwa-user`); await sleep(2500);
  await a.go(`${A}/dashboard`); await sleep(1500);
  ok(await a.ev(`typeof document.querySelector('script[src*="pwa"]')`) === 'object', 'pwa.js eingebunden');
  await a.ev(`document.querySelector('a.nav-ico[href="/topics"]').click()`);
  ok(await a.ev(`!!document.querySelector('.nav-progress.on')`), 'Klick auf Menülink zeigt den Ladebalken');
  await sleep(2500);
  ok(await a.ev(`location.pathname`) === '/topics' && await a.ev(`!document.querySelector('.nav-progress.on')`), 'Navigation ans Ziel, Balken weg');
  // Notausgang: tote Sitzung simulieren (Blazor-Navigation blockieren)
  await a.ev(`Blazor.addEventListener && 0; window.__blk=(e)=>{};document.addEventListener('click',e=>{const x=e.target.closest('a[href="/dashboard"]');if(x){e.stopImmediatePropagation();e.preventDefault();}},true)`);
  await a.ev(`document.querySelector('a.nav-ico[href="/dashboard"]').click()`);
  await sleep(5500);
  ok(await a.ev(`location.pathname`) === '/dashboard', 'Notausgang: blockierte Navigation wird nach 4 s hart geöffnet');
  const errs = a.errors; ok(errs.length === 0, `Keine JavaScript-Fehler (${errs.length}) ${errs.slice(0, 3).join(' | ')}`);
} catch (e) { console.log('SKRIPTFEHLER', e); await finish(1); }
await finish();
