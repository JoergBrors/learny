// Verbindungsanzeige und Wiederverbindung der Blazor-Sitzung (Circuit), mit Fokus auf mobile PWAs (Android/iOS).
// Ablauf (docs/architecture.md, „Wiederverbindung und PWA“):
//  1. Blazor meldet den Verbindungsverlust (components-reconnect-state-changed) — oder ein Lebenszeichen-Test nach der Rückkehr in den
//     Vordergrund bleibt unbeantwortet (iOS/Android halten WebSockets im Hintergrund an; die Verbindung wirkt offen, ist aber tot).
//  2. Genau EINE Wiederherstellung läuft (kein paralleles Neuverbinden): Server per /health prüfen (Backoff 2 → 30 s) → Blazor.reconnect() →
//     Blazor.resumeCircuit() → ist die Sitzung weg (Neustart, Aufbewahrungszeit abgelaufen): Seite kontrolliert neu laden.
//  3. Neuladen ist begrenzt (höchstens 3 in 60 s, danach verständliche Meldung statt Schleife; Neuversuch bei Netzrückkehr/Vordergrund/Klick).
// Keine Tokens, keine Anmeldedaten: der Zähler liegt nur in sessionStorage und enthält Zeitstempel.
const modal = document.getElementById("components-reconnect-modal");
const titleLine = document.getElementById("components-reconnect-title");
const statusLine = document.getElementById("components-reconnect-status");
const retryButton = document.getElementById("components-reconnect-button");

const PROBE_TIMEOUT_MS = 3000;
const BACKOFF_MS = [2000, 2000, 4000, 8000, 15000, 30000];
const PING_TIMEOUT_MS = 8000;          // Lebenszeichen der Sitzung nach Rückkehr in den Vordergrund
const BACKGROUND_MIN_MS = 5000;        // kürzere Wechsel (App-Umschalter) lösen keine Prüfung aus
const RELOAD_WINDOW_MS = 60000, RELOAD_MAX = 3, MIN_PAGE_AGE_MS = 8000;
const GUARD_KEY = "lc-reconnect-reloads";

let running = false;       // eine Wiederherstellung läuft
let pinging = false;       // ein Lebenszeichen-Test läuft
let blazorRetrying = false;   // Blazor führt gerade selbst Wiederverbindungsversuche aus
let hiddenAt = 0;
let stateSince = 0;       // Zeitpunkt der letzten Blazor-Statusmeldung
let wake = null;           // weckt die wartende Wiederherstellung sofort (Netz zurück, Vordergrund, Klick)

const sleep = (ms) => new Promise((resolve) => { const t = setTimeout(() => { wake = null; resolve(); }, ms); wake = () => { clearTimeout(t); wake = null; resolve(); }; });
const visible = () => document.visibilityState === "visible";
function nudge() { if (wake) wake(); }

function show(title, text, retry) {
    if (title && titleLine) titleLine.textContent = title;
    if (statusLine) statusLine.textContent = text;
    if (retryButton) retryButton.hidden = !retry;
    if (!modal.open) { try { modal.showModal(); } catch { /* Dialog nicht verfügbar: Text bleibt unsichtbar, Wiederherstellung läuft trotzdem */ } }
}
function hide() {
    clearGuard();
    if (modal.open) modal.close();
}

// ─── Reload-Schutz ───────────────────────────────────────────────────────────
function readGuard() {
    try {
        const g = JSON.parse(sessionStorage.getItem(GUARD_KEY) || "null");
        if (g && Array.isArray(g.t)) return g.t.filter((x) => Date.now() - x < RELOAD_WINDOW_MS);
    } catch { /* kein sessionStorage (privater Modus) */ }
    return [];
}
function writeGuard(times) { try { sessionStorage.setItem(GUARD_KEY, JSON.stringify({ t: times })); } catch { /* ignorieren */ } }
function clearGuard() { try { sessionStorage.removeItem(GUARD_KEY); } catch { /* ignorieren */ } }

async function reloadControlled() {
    const times = readGuard();
    if (times.length >= RELOAD_MAX) {
        show("Sitzung nicht wiederhergestellt", "Die Sitzung konnte nicht neu gestartet werden. Learny versucht es erneut, sobald die Verbindung wieder stabil ist — oder tippe auf „Erneut verbinden“.", true);
        return true;
    }
    // Anmeldung abgelaufen? Dann führt das Neuladen über den regulären Anmeldeprozess (kein Umgehen, keine Tokens im Browser).
    let signedOut = false;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), PROBE_TIMEOUT_MS);
    try {
        const r = await fetch("/api/mcp/whoami", { cache: "no-store", credentials: "same-origin", signal: controller.signal });
        signedOut = r.status === 401;
    } catch { return false; /* Netz weg oder hängende Verbindung: kein Neuladen in eine Fehlerseite, die Wiederherstellung versucht es erneut */ }
    finally { clearTimeout(timer); }
    writeGuard([...times, Date.now()]);
    show(signedOut ? "Erneute Anmeldung erforderlich" : "Sitzung wird neu gestartet",
        signedOut ? "Deine Anmeldung ist abgelaufen — du wirst zur Anmeldung weitergeleitet …" : "Die alte Sitzung existiert nicht mehr — Learny lädt neu …", false);
    // Mindestalter der Seite: schützt auch ohne sessionStorage vor schnellen Neuladeketten
    const wait = Math.max(600, MIN_PAGE_AGE_MS - performance.now());
    await sleep(wait);
    location.reload();
    return true;
}

// ─── Wiederherstellung (immer nur eine) ──────────────────────────────────────
async function isServerUp() {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), PROBE_TIMEOUT_MS);
    try {
        const response = await fetch("/health", { cache: "no-store", credentials: "omit", signal: controller.signal });
        return response.ok;
    } catch {
        return false;
    } finally {
        clearTimeout(timer);
    }
}

async function recover() {
    if (running) { nudge(); return; }
    running = true;
    try {
        show("Verbindung unterbrochen", "Learny verbindet sich automatisch neu …", false);
        for (let attempt = 0; ; attempt++) {
            while (!visible()) await sleep(60000);   // im Hintergrund nicht fragen (spart Akku); der Vordergrundwechsel weckt sofort
            if (navigator.onLine === false) {
                show("Offline", "Du bist offline. Learny verbindet sich automatisch, sobald das Netz zurück ist.", true);
                await sleep(BACKOFF_MS[Math.min(attempt, BACKOFF_MS.length - 1)]);
                continue;
            }
            if (!(await isServerUp())) {
                show("Server nicht erreichbar", "Der Server ist momentan nicht erreichbar. Learny versucht es automatisch weiter …", true);
                await sleep(BACKOFF_MS[Math.min(attempt, BACKOFF_MS.length - 1)]);
                continue;
            }
            show("Verbindung wird wiederhergestellt", "Die Sitzung wird wiederhergestellt …", false);
            // Der Rückgabewert allein genügt nicht: Nach einem Serverneustart meldet Blazor.reconnect() „true“, obwohl die Sitzung nicht mehr existiert
            // (nachgewiesen im Browsertest). Erst das Lebenszeichen der Sitzung beendet die Wiederherstellung.
            // Auf einer toten, scheinbar offenen Verbindung kann ein Aufruf ohne Ergebnis hängen: darum mit Zeitlimit.
            let connected = false;
            try { connected = await limited(Blazor.reconnect()); } catch { /* wie false */ }
            if (connected) {
                if (await pingCircuit() === "alive") { hide(); return; }
                // Verbindung steht, die Sitzung fehlt (Serverneustart): Wiederaufnahme ist ohne gespeicherten Zustand aussichtslos → neu laden
            } else {
                try { if (await limited(Blazor.resumeCircuit()) && await pingCircuit() === "alive") { hide(); return; } } catch { /* weiter */ }
            }
            if (await reloadControlled()) return;   // Sitzung existiert nicht mehr; false = Netz hing, nächster Durchlauf
        }
    } finally {
        running = false;
    }
}

// ─── Blazor-Ereignisse ───────────────────────────────────────────────────────
modal.addEventListener("components-reconnect-state-changed", (event) => {
    const state = event.detail.state;
    blazorRetrying = state === "show" || state === "retrying";
    stateSince = Date.now();
    if (state === "hide") { hide(); }
    else if (blazorRetrying) { if (!running) { if (navigator.onLine === false) show("Offline", "Du bist offline. Learny verbindet sich automatisch, sobald das Netz zurück ist.", true); else show("Verbindung unterbrochen", "Learny verbindet sich automatisch neu …", false); } }   // Blazor versucht es selbst: nicht parallel eingreifen
    else if (state === "rejected") { running ? nudge() : reloadControlled().then((done) => { if (!done) recover(); }); }   // Server kennt die Sitzung nicht mehr
    else { recover(); }                                                          // failed | paused | resume-failed: Blazor hat aufgegeben
});
retryButton.addEventListener("click", () => { clearGuard(); recover(); });

// ─── Lebenszeichen nach Rückkehr in den Vordergrund (mobile PWA) ─────────────
// Statischer Aufruf [JSInvokable("Ping")] in CircuitPing.cs: ohne Sitzung (statische Seiten) oder bei geschlossener Verbindung schlägt er sofort fehl
// (dann ist Blazor zuständig bzw. es gibt nichts zu tun); nur eine NICHT beantwortete Anfrage gilt als tote, scheinbar offene Verbindung.
const limited = (promise) => Promise.race([promise, new Promise((resolve) => setTimeout(() => resolve(false), 6000))]);

// Ergebnis: "alive" (Sitzung antwortet) | "failed" (sofortiger Fehler: keine Sitzung/Verbindung zu) | "silent" (keine Antwort: tote, scheinbar offene Verbindung)
async function pingCircuit() {
    if (!window.DotNet || typeof DotNet.invokeMethodAsync !== "function") return "failed";
    let timer;
    try {
        return await Promise.race([
            Promise.resolve().then(() => DotNet.invokeMethodAsync("LearnCards.Web", "Ping")).then(() => "alive", () => "failed"),
            new Promise((resolve) => { timer = setTimeout(() => resolve("silent"), PING_TIMEOUT_MS); }),
        ]);
    } finally {
        clearTimeout(timer);
    }
}

// Tote, scheinbar offene Verbindung (iOS/Android nach Hintergrund): Blazor erkennt sie selbst nach dem SignalR-Timeout (≈30 s) und wechselt dann in
// die eigene Wiederverbindung. Reagiert es binnen SILENT_GRACE_MS nicht, wird kontrolliert neu geladen (Reload-Schutz gilt).
const SILENT_GRACE_MS = 12000;
async function handleSilent() {
    running = true;
    try {
        show("Verbindung wird geprüft", "Die Verbindung antwortet nicht — Learny prüft und verbindet sich neu …", false);
        const since = Date.now();
        while (Date.now() - since < SILENT_GRACE_MS) {
            await sleep(1000);
            if (blazorRetrying) { show("Verbindung unterbrochen", "Learny verbindet sich automatisch neu …", false); return; }   // Blazor hat den Verlust bemerkt und übernimmt
            if (await pingCircuit() === "alive") { hide(); return; }     // war nur kurz träge
        }
    } finally {
        running = false;
    }
    recover();   // Blazor hat nicht reagiert: reguläre Wiederherstellung (Server prüfen → verbinden → ggf. kontrolliert neu laden)
}

async function checkCircuit() {
    if (pinging || running || modal.open) return;
    pinging = true;
    try {
        const outcome = await pingCircuit();
        if (outcome === "alive") clearGuard();
        else if (outcome === "silent") await handleSilent();
    } finally {
        pinging = false;
    }
}

function onForeground() {
    if (!visible()) return;
    if (running) { nudge(); return; }
    if (modal.open) { if (!blazorRetrying) recover(); return; }
    if (!hiddenAt || Date.now() - hiddenAt >= BACKGROUND_MIN_MS) checkCircuit();
}
document.addEventListener("visibilitychange", () => { if (visible()) onForeground(); else hiddenAt = Date.now(); });
window.addEventListener("pageshow", (e) => { if (e.persisted) { hiddenAt = 0; onForeground(); } });   // iOS: Seite aus dem Zurück-Vorwärts-Cache
window.addEventListener("online", () => {
    if (running) nudge();
    else if (modal.open) { if (blazorRetrying) show("Verbindung wird wiederhergestellt", "Das Netz ist wieder da — Learny verbindet sich neu …", false); else recover(); }
    else checkCircuit();
});
window.addEventListener("offline", () => { if (modal.open) show("Offline", "Du bist offline. Learny verbindet sich automatisch, sobald das Netz zurück ist.", true); });

// Wächter: Hängt ein Blazor-Neuverbindungsversuch (z. B. Verhandlung ohne Antwort im Mobilnetz), greift die eigene Wiederherstellung ein,
// sobald der Server laut /health erreichbar ist. Sie wird ohnehin nie parallel gestartet (running).
const STUCK_MS = 20000;
setInterval(() => {
    if (modal.open && blazorRetrying && !running && visible() && Date.now() - stateSince > STUCK_MS) { blazorRetrying = false; recover(); }
}, 5000);

// Nach dem Start: erstes Lebenszeichen setzt den Reload-Zähler zurück (nur wenn die Seite eine Sitzung hat)
setTimeout(() => { if (visible()) checkCircuit(); }, 4000);
