// PWA-Bedienung (iOS/Android, installierte App): Herunterziehen zum Aktualisieren und robuste Menünavigation.
// Beides ist reine Bedienhilfe im Browser: keine Daten, keine Tokens, kein Serveraufruf außer dem normalen Neuladen/Navigieren.
(function () {
    var standalone = (window.navigator && navigator.standalone === true) || (window.matchMedia && matchMedia('(display-mode: standalone)').matches);

    // ─── Navigationsanzeige und Notausgang ───
    // Blazor fängt Links ab und navigiert über die Sitzung. Ist die (im Hintergrund eingefrorene) Verbindung tot, passiert nichts — der Link „geht nicht“.
    // Darum: Ladebalken ab dem Klick; ändert sich die Adresse binnen NAV_FALLBACK_MS nicht, wird der Link normal (hart) geöffnet.
    var NAV_FALLBACK_MS = 4000, bar = null, timer = null;
    function barEl() { if (!bar) { bar = document.createElement('div'); bar.className = 'nav-progress'; bar.setAttribute('aria-hidden', 'true'); document.body.appendChild(bar); } return bar; }
    function stopBar() { if (timer) { clearTimeout(timer); timer = null; } if (bar) bar.classList.remove('on'); }
    document.addEventListener('click', function (e) {
        if (e.defaultPrevented === false && e.button > 0) return;
        var a = e.target.closest ? e.target.closest('a[href]') : null;
        if (!a || a.target === '_blank' || a.hasAttribute('download') || e.ctrlKey || e.metaKey || e.shiftKey) return;
        var url; try { url = new URL(a.href, location.href); } catch (x) { return; }
        if (url.origin !== location.origin || url.pathname.indexOf('/auth/') === 0 || url.pathname.indexOf('/api/') === 0) return;
        if (url.pathname + url.search === location.pathname + location.search) return;
        var from = location.pathname + location.search;
        barEl().classList.add('on');
        if (timer) clearTimeout(timer);
        timer = setTimeout(function () {
            timer = null;
            if (location.pathname + location.search === from && document.visibilityState === 'visible') location.assign(url.href);   // Notausgang: normale Seitennavigation
            else stopBar();
        }, NAV_FALLBACK_MS);
    }, true);
    window.lcOnEnhanced && window.lcOnEnhanced(stopBar);
    window.addEventListener('pageshow', stopBar);
    // Adressänderung (Blazor-Navigation) beendet die Anzeige, ohne auf das Ereignis zu warten
    var lastUrl = location.href;
    setInterval(function () { if (location.href !== lastUrl) { lastUrl = location.href; stopBar(); } }, 250);

    // ─── Herunterziehen zum Aktualisieren (iOS-App hat keine eigene Geste) ───
    if (!standalone) return;
    var THRESHOLD = 80, MAX = 120, startY = 0, pull = 0, active = false, ind = null;
    function indEl() {
        if (!ind) { ind = document.createElement('div'); ind.className = 'ptr'; ind.setAttribute('aria-hidden', 'true'); ind.textContent = '↓'; document.body.appendChild(ind); }
        return ind;
    }
    function blocked(t) {
        // Nie in Eingaben, Overlays, Dialogen oder Editoren: ein Neuladen würde ungespeicherte Eingaben verwerfen.
        if (t.closest && t.closest('input, textarea, select, [contenteditable], .drawer, dialog, .preview-overlay, .rte')) return true;
        return /\/(edit|preview)(\/|$)/.test(location.pathname) || /\/(quiz|exam|learn)(\/|$)/.test(location.pathname);
    }
    function scrolledTop() {
        var el = document.scrollingElement || document.documentElement;
        return (window.scrollY || el.scrollTop || 0) <= 0;
    }
    document.addEventListener('touchstart', function (e) {
        active = false; pull = 0;
        if (e.touches.length !== 1 || !scrolledTop() || blocked(e.target)) return;
        startY = e.touches[0].clientY; active = true;
    }, { passive: true });
    document.addEventListener('touchmove', function (e) {
        if (!active) return;
        var dy = e.touches[0].clientY - startY;
        if (dy <= 0 || !scrolledTop()) { pull = 0; indEl().classList.remove('show'); return; }
        pull = Math.min(MAX, dy * 0.5);
        var i = indEl(); i.classList.add('show'); i.style.transform = 'translate(-50%,' + (pull - 40) + 'px)';
        i.textContent = pull >= THRESHOLD * 0.5 ? '↻' : '↓';
        i.classList.toggle('ready', pull >= THRESHOLD * 0.5);
    }, { passive: true });
    function end() {
        if (!active) return; active = false;
        var go = pull >= THRESHOLD * 0.5; pull = 0;
        if (ind) { ind.classList.remove('show'); ind.style.transform = ''; }
        if (go) { if (ind) { ind.classList.add('show', 'busy'); } location.reload(); }
    }
    document.addEventListener('touchend', end, { passive: true });
    document.addEventListener('touchcancel', function () { active = false; pull = 0; if (ind) ind.classList.remove('show'); }, { passive: true });
})();
