// Verbindungsanzeige (Blazor-Circuit) mit aktiver Erkennung, ob der Server wieder da ist.
// Blazor versucht selbst nur in großen Abständen neu und gibt irgendwann auf. Zusätzlich fragt diese Datei bei unterbrochener Verbindung
// alle 2 Sekunden den schlanken Endpunkt /health ab (ohne Zwischenspeicher, ohne Anmeldung, ohne Daten). Antwortet er, wird zuerst die
// bestehende Sitzung wieder aufgenommen, sonst die Seite neu geladen — ohne dass jemand auf „Retry“ klicken muss.
const reconnectModal = document.getElementById("components-reconnect-modal");
const statusLine = document.getElementById("components-reconnect-status");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

document.getElementById("components-reconnect-button").addEventListener("click", () => { startProbing(); rejoin(); });
document.getElementById("components-resume-button").addEventListener("click", () => { startProbing(); rejoin(); });
document.getElementById("components-reload-button").addEventListener("click", () => location.reload());

const PROBE_INTERVAL_MS = 2000;
const PROBE_TIMEOUT_MS = 2000;
let probing = false;
let attempts = 0;

function setStatus(text) { if (statusLine) statusLine.textContent = text; }
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function handleReconnectStateChanged(event) {
    const state = event.detail.state;
    if (state === "show") {
        reconnectModal.showModal();
        startProbing();
    } else if (state === "hide") {
        stopProbing();
        reconnectModal.close();
    } else if (state === "failed") {
        startProbing();
    } else if (state === "rejected") {
        location.reload();
    }
}

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

function stopProbing() {
    probing = false;
    attempts = 0;
    setStatus("");
}

async function startProbing() {
    if (probing) return;
    probing = true;
    attempts = 0;
    while (probing) {
        attempts++;
        if (await isServerUp()) {
            if (!probing) return;
            probing = false;
            setStatus("Der Server ist wieder erreichbar — Sitzung wird wiederhergestellt …");
            await rejoin();
            return;
        }
        setStatus(`Server nicht erreichbar — automatische Prüfung, Versuch ${attempts} (alle ${PROBE_INTERVAL_MS / 1000} s).`);
        await sleep(PROBE_INTERVAL_MS);
    }
}

async function rejoin() {
    try {
        // true = Verbindung wieder da; false = Server erreicht, aber die Sitzung existiert nicht mehr; Ausnahme = nicht erreichbar
        if (await Blazor.reconnect()) return;
    } catch { /* weiter unten */ }
    try {
        if (await Blazor.resumeCircuit()) { reconnectModal.close(); return; }
    } catch { /* weiter unten */ }
    // Nach einem Neustart des Servers gibt es die alte Sitzung nicht mehr: Seite neu laden (der Server ist laut /health erreichbar).
    if (await isServerUp()) location.reload();
    else startProbing();
}

// Zurück im Tab oder Netz wieder da: sofort prüfen (statt auf das nächste Intervall zu warten)
document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible" && reconnectModal.open) startProbing(); });
window.addEventListener("online", () => { if (reconnectModal.open) startProbing(); });
