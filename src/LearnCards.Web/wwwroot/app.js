// LearnCards — kleine JS-Helfer (Darstellung: System / Hell / Dunkel)
// Modus „system“ (Standard) folgt der Einstellung des Geräts (prefers-color-scheme) und reagiert live auf deren Änderung;
// „light“ und „dark“ sind ausdrückliche Wahlen. Gespeichert wird der Modus (Browser: localStorage, angemeldet zusätzlich im Profil).
window.lcTheme = (function () {
    var KEY = 'lc-theme';
    var mq = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
    var root = document.documentElement;
    function valid(m) { return m === 'system' || m === 'light' || m === 'dark'; }
    function stored() { try { var v = localStorage.getItem(KEY); return valid(v) ? v : 'system'; } catch (e) { return 'system'; } }
    function resolve(mode) { return mode === 'system' ? (mq && mq.matches ? 'dark' : 'light') : mode; }
    function apply(mode) { root.dataset.themeMode = mode; root.dataset.theme = resolve(mode); }
    function onSystemChange() { if (stored() === 'system') apply('system'); }
    if (mq) { if (mq.addEventListener) mq.addEventListener('change', onSystemChange); else if (mq.addListener) mq.addListener(onSystemChange); }
    return {
        mode: stored,
        getStored: stored,
        isDark: function () { return root.dataset.theme === 'dark'; },
        get: function () { return root.dataset.theme === 'dark' ? 'dark' : 'light'; },
        set: function (mode) {
            if (!valid(mode)) mode = 'system';
            try { localStorage.setItem(KEY, mode); } catch (e) { /* privater Modus: gilt nur für diese Sitzung */ }
            apply(mode);
        },
        apply: apply
    };
})();

// Begrüßung: lokale Tageszeit des Browsers (nur Anzeige)
window.lcDash = { hour: function () { return new Date().getHours(); } };

// Zuletzt verwendete Eingabemethode (touch | pen | mouse | keyboard) als data-input am <html>: steuert nur sichtbare Hinweise (Wischhinweis, Tastaturkürzel).
// Es wird nie eine Eingabeart deaktiviert — ein Touch-Laptop kann jederzeit zwischen Touch, Maus und Tastatur wechseln.
(function () {
    var root = document.documentElement;
    function set(m) { if (root.dataset.input !== m) root.dataset.input = m; }
    set(window.matchMedia && matchMedia('(pointer: coarse)').matches ? 'touch' : 'mouse');
    addEventListener('pointerdown', function (e) { set(e.pointerType === 'touch' ? 'touch' : e.pointerType === 'pen' ? 'pen' : 'mouse'); }, true);
    addEventListener('keydown', function (e) { if (e.key === 'Tab' || e.key.indexOf('Arrow') === 0 || e.key === ' ' || e.key === 'Enter') set('keyboard'); }, true);
})();

// Shell-Bedienung ohne Blazor-Ereignisse: Das Layout wird statisch gerendert (nur die Seiten sind interaktiv) — "Mehr", Profilmenü und Darstellung funktionieren daher per JavaScript.
(function () {
    var LABEL = { system: ['🖥️', 'Auto', 'System (automatisch, folgt dem Gerät)'], light: ['☀️', 'Hell', 'Hell'], dark: ['🌙', 'Dunkel', 'Dunkel'] };
    var ORDER = ['system', 'light', 'dark'];
    function syncTheme() {
        var m = window.lcTheme.mode(), l = LABEL[m] || LABEL.system, next = LABEL[ORDER[(ORDER.indexOf(m) + 1) % 3]][2];
        document.querySelectorAll('[data-theme-icon]').forEach(function (e) { e.textContent = l[0]; });
        document.querySelectorAll('[data-theme-short]').forEach(function (e) { e.textContent = l[1]; });
        document.querySelectorAll('button[data-shell="theme"]').forEach(function (b) { var t = 'Darstellung: ' + l[2] + '. Klicken für: ' + next; b.title = t; b.setAttribute('aria-label', t); });
        return l[2];
    }
    function closeAll(except) {
        var nav = document.getElementById('main-nav');
        if (nav && nav !== except) { nav.classList.remove('more-open'); var mb = nav.querySelector('[data-shell="more"]'); if (mb) mb.setAttribute('aria-expanded', 'false'); }
        document.querySelectorAll('.user-menu').forEach(function (um) {
            if (um === except) return;
            var p = um.querySelector('.user-panel'), b = um.querySelector('[data-shell="menu"]');
            if (p) p.hidden = true; if (b) b.setAttribute('aria-expanded', 'false');
        });
    }
    document.addEventListener('click', function (e) {
        var t = e.target.closest ? e.target.closest('[data-shell]') : null;
        if (!t) { if (!(e.target.closest && e.target.closest('.user-panel, .nav-secondary-wrap'))) closeAll(); else if (e.target.closest('a')) closeAll(); return; }
        var kind = t.getAttribute('data-shell');
        if (kind === 'more') {
            var nav = document.getElementById('main-nav'), open = !nav.classList.contains('more-open');
            closeAll(nav); nav.classList.toggle('more-open', open); t.setAttribute('aria-expanded', open ? 'true' : 'false');
        } else if (kind === 'menu') {
            var um = t.closest('.user-menu'), p = um.querySelector('.user-panel'), show = p.hidden;
            closeAll(um); p.hidden = !show; t.setAttribute('aria-expanded', show ? 'true' : 'false');
        } else if (kind === 'theme') {
            var m = window.lcTheme.mode(); window.lcTheme.set(ORDER[(ORDER.indexOf(m) + 1) % 3]);
            var a = document.getElementById('theme-announce'); var lab = syncTheme(); if (a) a.textContent = 'Darstellung: ' + lab;
        }
    });
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeAll(); });
    function init() { syncTheme(); }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init); else init();
    // Nach Blazor-Navigation/Enhanced-Updates Beschriftungen angleichen
    if (window.Blazor && Blazor.addEventListener) Blazor.addEventListener('enhancedload', init);
})();
