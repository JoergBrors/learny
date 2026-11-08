// LearnCards — Lernkarten-Steuerung: Wischen, Tippen, Pfeiltasten (Pointer Events, keine Bibliothek).
// Grundsätze: Vertikales Scrollen hat Vorrang und bleibt nativ (kein preventDefault, touch-action: pan-y nur an der Kartenfläche). Ein Kartenwechsel entsteht
// nur bei eindeutig horizontaler Bewegung; Diagonalen und vertikale Züge wechseln nie. Interaktive Elemente und horizontal scrollbare Bereiche lösen keine Gesten aus.
// Jede Geste hat eine Schaltflächen- und Tastaturalternative. Die Auswertung (classify/shouldCommit/isTap) ist rein und separat testbar (tests/browser/cards-gesture-unit.js).
(function (root) {
    'use strict';
    var T = { pendingPx: 10, swipeMinPx: 24, horizRatio: 1.8, vertRatio: 0.6, giveUpPx: 48, commitPx: 64, commitFraction: 0.18, tapMovePx: 10, tapMaxMs: 600, flingVelocity: 0.5, flingMinPx: 30 };

    /** 'pending' (noch unklar) | 'swipe' (eindeutig horizontal) | 'scroll' (vertikal/diagonal: nativ scrollen lassen) | 'none' (unklar und weit bewegt: ignorieren) */
    function classify(dx, dy) {
        var ax = Math.abs(dx), ay = Math.abs(dy);
        if (ax < T.pendingPx && ay < T.pendingPx) return 'pending';
        if (ax >= T.swipeMinPx && ax > ay * T.horizRatio) return 'swipe';
        if (ay >= T.pendingPx && ay > ax * T.vertRatio) return 'scroll';
        if (ax > T.giveUpPx || ay > T.giveUpPx) return 'none';
        return 'pending';
    }
    function shouldCommit(dx, dtMs, width) {
        var ax = Math.abs(dx), v = ax / Math.max(1, dtMs);
        return ax >= Math.max(T.commitPx, (width || 0) * T.commitFraction) || (v >= T.flingVelocity && ax >= T.flingMinPx);
    }
    function isTap(dx, dy, dtMs) { return Math.abs(dx) < T.tapMovePx && Math.abs(dy) < T.tapMovePx && dtMs < T.tapMaxMs; }

    var api = { classify: classify, shouldCommit: shouldCommit, isTap: isTap, thresholds: T };
    if (typeof module !== 'undefined' && module.exports) module.exports = api;
    if (typeof document === 'undefined') return;

    var INTERACTIVE = 'a,button,input,textarea,select,summary,label,video,audio,iframe,canvas,[contenteditable],[data-no-swipe],pre,table';
    var EDITABLE = 'input,textarea,select,[contenteditable],[role="textbox"]';
    var DIALOGS = '[role="dialog"],dialog[open],.drawer,.preview-overlay';
    var states = new WeakMap();

    function insideInteractive(target, scene) {
        var hit = target && target.closest ? target.closest(INTERACTIVE) : null;
        if (hit && hit !== scene && scene.contains(hit)) return true;
        for (var n = target; n && n !== scene; n = n.parentElement) {   // horizontal scrollbare Bereiche (Codeblöcke, Tabellen) behalten ihre Geste
            if (n.nodeType !== 1 || n.classList.contains('flip-face') || n.classList.contains('flip-inner')) continue;
            var cs = getComputedStyle(n);
            if ((cs.overflowX === 'auto' || cs.overflowX === 'scroll') && n.scrollWidth > n.clientWidth + 1) return true;
        }
        return false;
    }

    function attach(scene, dotnet) {
        if (!scene || states.has(scene)) return;
        var st = { dotnet: dotnet, id: null, x0: 0, y0: 0, t0: 0, mode: 'none', ignore: false, dx: 0, last: 0 };
        states.set(scene, st);

        function reset(animated) {
            scene.classList.remove('is-swiping');
            if (animated) scene.classList.add('is-settling');
            scene.style.removeProperty('--swipe-x'); scene.style.removeProperty('--swipe-o');
            if (animated) setTimeout(function () { scene.classList.remove('is-settling'); }, 220);
            st.id = null; st.mode = 'none'; st.dx = 0;
        }
        function canGo(dx) { return dx < 0 ? scene.dataset.canNext === 'true' : scene.dataset.canPrev === 'true'; }

        st.down = function (e) {
            if (st.id !== null) { reset(false); st.ignore = true; return; }          // zweiter Finger (z. B. Zoom): keine Geste
            if (e.pointerType === 'mouse' && e.button !== 0) return;
            if (e.isPrimary === false) return;
            st.ignore = insideInteractive(e.target, scene);
            st.id = e.pointerId; st.x0 = e.clientX; st.y0 = e.clientY; st.t0 = e.timeStamp; st.mode = st.ignore ? 'none' : 'pending'; st.dx = 0;
        };
        st.move = function (e) {
            if (e.pointerId !== st.id || st.ignore) return;
            var dx = e.clientX - st.x0, dy = e.clientY - st.y0;
            if (st.mode === 'pending') {
                var c = classify(dx, dy);
                if (c === 'swipe' && e.pointerType !== 'mouse') { st.mode = 'swipe'; scene.classList.add('is-swiping'); }
                else if (c === 'scroll' || c === 'none') st.mode = c === 'scroll' ? 'scroll' : 'none';
                else if (c === 'swipe') st.mode = 'none';   // Maus: kein Wischen (Textmarkierung bleibt möglich)
            }
            if (st.mode === 'swipe') {
                st.dx = dx;
                var shown = canGo(dx) ? dx : dx * 0.25;   // am Anfang/Ende der Karten nur ein Widerstand
                scene.style.setProperty('--swipe-x', shown + 'px');
                scene.style.setProperty('--swipe-o', String(1 - Math.min(0.35, Math.abs(shown) / 700)));
            }
        };
        st.up = function (e) {
            if (e.pointerId !== st.id) return;
            var dx = e.clientX - st.x0, dy = e.clientY - st.y0, dt = e.timeStamp - st.t0, mode = st.mode, ignore = st.ignore;
            if (mode === 'swipe') {
                var go = shouldCommit(dx, dt, scene.clientWidth) && canGo(dx);
                reset(true);
                if (go) st.dotnet.invokeMethodAsync('OnSwipe', dx < 0 ? 'next' : 'prev').catch(function () { });
            } else {
                var tap = !ignore && (mode === 'pending' || mode === 'none') && isTap(dx, dy, dt);
                reset(false);
                if (tap) st.dotnet.invokeMethodAsync('OnTap').catch(function () { });
            }
            st.ignore = false;
        };
        st.cancel = function (e) { if (e.pointerId === st.id) { reset(true); st.ignore = false; } };   // z. B. Browser übernimmt das vertikale Scrollen
        st.key = function (e) {
            if (e.defaultPrevented || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
            if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
            var t = e.target;
            if (t && t.closest && (t.closest(EDITABLE) || t.closest(DIALOGS))) return;       // Texteingabe, Editoren, Dialoge behalten ihre Tasten
            if (document.querySelector(DIALOGS)) return;
            st.dotnet.invokeMethodAsync('OnSwipe', e.key === 'ArrowRight' ? 'next' : 'prev').catch(function () { });
        };
        scene.addEventListener('pointerdown', st.down);
        scene.addEventListener('pointermove', st.move);
        scene.addEventListener('pointerup', st.up);
        scene.addEventListener('pointercancel', st.cancel);
        scene.addEventListener('lostpointercapture', st.cancel);
        document.addEventListener('keydown', st.key);
    }

    function detach(scene) {
        var st = scene && states.get(scene);
        if (!st) return;
        scene.removeEventListener('pointerdown', st.down); scene.removeEventListener('pointermove', st.move); scene.removeEventListener('pointerup', st.up);
        scene.removeEventListener('pointercancel', st.cancel); scene.removeEventListener('lostpointercapture', st.cancel);
        document.removeEventListener('keydown', st.key);
        states.delete(scene);
    }

    /** Vollbildmodus: setzt data-immersive am <html> (blendet Shell per CSS aus) und nutzt, wo verfügbar, die Fullscreen-API (iOS-Safari: nur Ausblenden). */
    var immersiveRef = null;
    function immersive(on, dotnet) {
        var el = document.documentElement;
        if (on) { immersiveRef = dotnet; el.dataset.immersive = 'true'; try { if (el.requestFullscreen && !document.fullscreenElement) el.requestFullscreen({ navigationUI: 'hide' }).catch(function () { }); } catch (e) { } }
        else { delete el.dataset.immersive; immersiveRef = null; try { if (document.fullscreenElement && document.exitFullscreen) document.exitFullscreen().catch(function () { }); } catch (e) { } }
    }
    document.addEventListener('fullscreenchange', function () {
        if (!document.fullscreenElement && document.documentElement.dataset.immersive === 'true' && immersiveRef) {
            var r = immersiveRef; delete document.documentElement.dataset.immersive; immersiveRef = null;
            r.invokeMethodAsync('OnImmersiveExited').catch(function () { });
        }
    });

    api.immersive = immersive; api.attach = attach; api.detach = detach;
    root.lcCards = api;
})(typeof window !== 'undefined' ? window : globalThis);
