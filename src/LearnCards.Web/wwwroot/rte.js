// Visueller Editor für formatierte Kartentexte (ADR 0020) — ohne externe Bibliothek.
// Der Editor arbeitet im Browser mit contenteditable, gespeichert wird aber nie HTML, sondern das begrenzte Markup (siehe RichText.cs):
// jede Änderung wird hier in dieses Markup serialisiert, die Anwendung validiert und rendert es serverseitig neu.
window.lcRte = (function () {
    const editors = new Map();
    let guardOn = false, confirmedUntil = 0;
    document.addEventListener('click', e => {
        if (!guardOn) return;
        const a = e.target.closest && e.target.closest('a[href]');
        if (!a || a.target === '_blank' || a.hasAttribute('download')) return;
        let u;
        try { u = new URL(a.href); } catch { return; }
        if (u.origin !== location.origin || (u.pathname === location.pathname && u.search === location.search)) return;
        if (confirm('Es gibt ungespeicherte Änderungen. Seite wirklich verlassen?')) confirmedUntil = Date.now() + 3000;
        else { e.preventDefault(); e.stopImmediatePropagation(); }
    }, true);

    function escapeText(t) {
        return t.replace(/\u00a0/g, ' ').replace(/\\/g, '\\\\').replace(/\*/g, '\\*').replace(/\[/g, '\\[').replace(/\]/g, '\\]').replace(/\+(?=\+)|(?<=\+)\+/g, '\\+');
    }
    function isSafeUrl(u) {
        try { const x = new URL(u); return (x.protocol === 'https:' || x.protocol === 'http:') && !x.username && u.length <= 2000 && !/[\s<>"']/.test(u); } catch { return false; }
    }
    const BLOCK_TAGS = ['p', 'div', 'blockquote', 'pre'];

    // Formatierung wird von den Vorfahren auf jeden Textabschnitt übertragen: auch Listen/Absätze innerhalb eines <b> gehen so nicht verloren
    function nextFmt(n, fmt) {
        const tag = n.tagName.toLowerCase();
        const f = { ...fmt };
        const st = n.style || {};
        if (tag === 'b' || tag === 'strong' || st.fontWeight === 'bold' || parseInt(st.fontWeight || '0', 10) >= 600) f.b = true;
        if (tag === 'i' || tag === 'em' || st.fontStyle === 'italic') f.i = true;
        if (tag === 'u' || (st.textDecorationLine || st.textDecoration || '').includes('underline')) f.u = true;
        if (tag === 'a') { const h = n.getAttribute('href') || ''; if (isSafeUrl(h)) f.a = h; }
        return f;
    }
    function inlineSegs(el, fmt, out) {
        el.childNodes.forEach(n => {
            if (n.nodeType === 3) { out.push({ t: n.nodeValue.replace(/\n/g, ' '), ...fmt }); return; }
            if (n.nodeType !== 1) return;
            const tag = n.tagName.toLowerCase();
            if (tag === 'br') { out.push({ br: true }); return; }
            if (tag === 'ul' || tag === 'ol') return;
            inlineSegs(n, nextFmt(n, fmt), out);
        });
    }
    function sameMarks(a, b) { return !!a.b === !!b.b && !!a.i === !!b.i && !!a.u === !!b.u; }
    function emitRuns(segs) {
        let out = '', run = null;
        const closeRun = () => {
            if (!run) return;
            const txt = escapeText(run.t), core = txt.trim();
            if (!core) out += txt;
            else {
                let m = core;
                if (run.i) m = (run.b ? '_' : '*') + m + (run.b ? '_' : '*');
                if (run.b) m = '**' + m + '**';
                if (run.u) m = '++' + m + '++';
                out += txt.match(/^\s*/)[0] + m + txt.match(/\s*$/)[0];
            }
            run = null;
        };
        segs.forEach(s => {
            if (s.br) { closeRun(); out += '\n'; return; }
            if (run && sameMarks(run, s)) run.t += s.t; else { closeRun(); run = { t: s.t, b: s.b, i: s.i, u: s.u }; }
        });
        closeRun();
        return out;
    }
    function emit(segs) {
        // aufeinanderfolgende Abschnitte mit demselben Link zu einem Link zusammenfassen
        let out = '';
        let i = 0;
        while (i < segs.length) {
            const a = segs[i].a;
            let j = i;
            while (j < segs.length && !segs[j].br && segs[j].a === a) j++;
            if (j === i) { out += emitRuns([segs[i]]); i++; continue; }
            const inner = emitRuns(segs.slice(i, j));
            out += a && inner.trim() ? '[' + inner.replace(/[\[\]\n]/g, ' ') + '](' + a.replace(/\(/g, '%28').replace(/\)/g, '%29') + ')' : inner;
            i = j;
        }
        return out;
    }
    function toMarkup(root) {
        const blocks = [];
        let pend = [];
        const flush = () => {
            const text = emit(pend).replace(/\n+$/g, '');
            if (text.trim()) blocks.push({ kind: 'p', text });
            pend = [];
        };
        function walk(node, fmt) {
            node.childNodes.forEach(n => {
                if (n.nodeType === 3) { pend.push({ t: n.nodeValue.replace(/\n/g, ' '), ...fmt }); return; }
                if (n.nodeType !== 1) return;
                const tag = n.tagName.toLowerCase();
                if (tag === 'br') { pend.push({ br: true }); return; }
                const f = nextFmt(n, fmt);
                if (/^h[1-6]$/.test(tag)) {
                    flush();
                    const segs = []; inlineSegs(n, f, segs);
                    const t = emit(segs).replace(/\n/g, ' ').trim();
                    if (t) blocks.push({ kind: 'h', text: '#'.repeat(Math.min(parseInt(tag[1], 10), 3)) + ' ' + t });
                    return;
                }
                if (tag === 'ul' || tag === 'ol') {
                    flush();
                    let k = 0;
                    n.querySelectorAll(':scope > li').forEach(li => {
                        k++;
                        const segs = []; inlineSegs(li, f, segs);
                        const t = emit(segs).replace(/\n/g, ' ').trim();
                        if (t) blocks.push({ kind: 'li', text: (tag === 'ul' ? '- ' : k + '. ') + t, list: tag });
                    });
                    return;
                }
                if (BLOCK_TAGS.includes(tag)) { flush(); walk(n, f); flush(); return; }
                walk(n, f);   // Inline-Element (auch mit Blöcken darin): Formatierung weiterreichen
            });
        }
        walk(root, {});
        flush();
        let out = '';
        blocks.forEach((b, i) => {
            const prev = blocks[i - 1];
            if (i > 0) out += (b.kind === 'li' && prev.kind === 'li' && prev.list === b.list) ? '\n' : '\n\n';
            let text = b.text;
            if (b.kind === 'p') text = text.replace(/^(#{1,3}\s)/, '\$1').replace(/^-\s/, '\- ');
            out += text;
        });
        return out;
    }

    function state(id) { return editors.get(id); }
    function push(id) {
        const st = state(id);
        if (!st) return;
        clearTimeout(st.timer);
        st.ref.invokeMethodAsync('OnEditorChanged', toMarkup(st.el));
    }
    function restore(st) {
        st.el.focus();
        if (st.range) { const sel = window.getSelection(); sel.removeAllRanges(); sel.addRange(st.range); }
    }

    return {
        init(id, html, ref) {
            const el = document.getElementById(id);
            if (!el) return;
            el.innerHTML = html;
            const st = { el, ref, timer: null, range: null };
            editors.set(id, st);
            el.addEventListener('input', () => { clearTimeout(st.timer); st.timer = setTimeout(() => push(id), 250); });
            el.addEventListener('blur', () => push(id));
            el.addEventListener('paste', e => {   // nur Klartext einfügen: fremdes HTML gelangt nie in den Editor
                e.preventDefault();
                const t = (e.clipboardData || window.clipboardData).getData('text/plain');
                document.execCommand('insertText', false, t);
            });
            el.addEventListener('drop', e => e.preventDefault());
            document.addEventListener('selectionchange', () => {
                const sel = window.getSelection();
                if (sel.rangeCount && el.contains(sel.anchorNode)) st.range = sel.getRangeAt(0).cloneRange();
            });
        },
        setHtml(id, html) { const st = state(id); if (st) { st.el.innerHTML = html; st.range = null; } },
        exec(id, cmd) { const st = state(id); if (!st) return; restore(st); document.execCommand(cmd, false, null); push(id); },
        block(id, tag) { const st = state(id); if (!st) return; restore(st); document.execCommand('formatBlock', false, tag); push(id); },
        link(id, url) {
            const st = state(id);
            if (!st) return false;
            if (!isSafeUrl(url)) return false;
            restore(st);
            const sel = window.getSelection();
            if (sel.isCollapsed) {
                const a = document.createElement('a');
                a.href = url; a.textContent = url;
                sel.getRangeAt(0).insertNode(a);
            } else document.execCommand('createLink', false, url);
            push(id);
            return true;
        },
        clear(id) { const st = state(id); if (!st) return; restore(st); document.execCommand('removeFormat'); document.execCommand('formatBlock', false, 'p'); push(id); },
        focus(id) { const st = state(id); if (st) st.el.focus(); },
        flush(id) { push(id); },
        dispose(id) { editors.delete(id); },
        toMarkup,
        // Schutz vor Datenverlust: native Warnung beim Schließen/Neuladen und Rückfrage bei Klicks auf interne Links, solange ungespeicherte Änderungen existieren
        setBeforeUnload(on) {
            guardOn = on;
            window.onbeforeunload = on ? (e => { e.preventDefault(); e.returnValue = ''; return ''; }) : null;
        },
        wasConfirmed() { return Date.now() < confirmedUntil; },
    };
})();
