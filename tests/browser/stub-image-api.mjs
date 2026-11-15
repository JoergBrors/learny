// Minimaler OpenAI-Bild-Stub für die Browserprüfung der KI-Bildavatare (ADR 0056). KEIN echter Dienst: liefert einfarbige PNG-Platzhalter je Stil/Quelle.
// Start: node tests/browser/stub-image-api.mjs [port]  (Standard 5298); die Instanz mit OPENAI_API_KEY=stub-key OPENAI_BASE_URL=http://127.0.0.1:5298/v1 starten.
// Stimmungsaufträge (ADR 0058) erkennt der Stub an „Expression key: <mood>“ im Auftrag. Steuerung: GET /__fail?n=1 → die nächsten n Aufrufe antworten mit 500; GET /__log → Liste der bisherigen Aufrufe (Pfad, Modell, Foto ja/nein, Stil, Wunsch).
import { createServer } from 'node:http';
import { deflateSync, crc32 } from 'node:zlib';

const port = Number(process.argv[2] ?? 5298);
let failNext = 0, counter = 0; const log = [];

function png(w, h, [r, g, b]) {
  const row = Buffer.alloc(1 + w * 3); for (let x = 0; x < w; x++) { row[1 + x * 3] = r; row[2 + x * 3] = g; row[3 + x * 3] = b - (x % 40); }
  const raw = Buffer.concat(Array.from({ length: h }, (_, y) => { const c = Buffer.from(row); for (let x = 0; x < w; x++) c[1 + x * 3] = Math.max(0, r - (y % 50)); return c; }));
  const chunk = (t, d) => { const len = Buffer.alloc(4); len.writeUInt32BE(d.length); const td = Buffer.concat([Buffer.from(t), d]); const c = Buffer.alloc(4); c.writeUInt32BE(crc32(td) >>> 0); return Buffer.concat([len, td, c]); };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 2;
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]), chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}

createServer((req, res) => {
  const url = new URL(req.url, 'http://x');
  if (url.pathname === '/__fail') { failNext = Number(url.searchParams.get('n') ?? 1); res.end('ok'); return; }
  if (url.pathname === '/__log') { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(log)); return; }
  const chunks = []; req.on('data', c => chunks.push(c));
  req.on('end', () => {
    let body = Buffer.concat(chunks).toString('latin1');
    try { if ((req.headers['content-type'] ?? '').includes('json')) body = JSON.parse(Buffer.concat(chunks).toString('utf8')).prompt + ' ' + body; } catch { /* kein JSON */ }
    const edit = url.pathname.endsWith('/images/edits');
    const model = /gpt-image[\w.-]+/.exec(body)?.[0] ?? '';
    const style = body.includes('halftone') ? 'classic' : body.includes('3D cartoon') ? 'cartoon3d' : 'realistic';
    const wish = /<wish>([^<]*)<\/wish>/.exec(body)?.[1] ?? '';
    const mood = /Expression key: (\w+)/.exec(body)?.[1] ?? '';
    log.push({ path: url.pathname, model, photo: edit && /name="?image"?/.test(body), style, wish, mood });
    if (failNext > 0) { failNext--; res.statusCode = 500; res.end('{"error":{"message":"stub"}}'); return; }
    const color = { classic: [214, 90, 60], cartoon3d: [70, 160, 210], realistic: [120, 90, 170] }[style];
    if (edit) color[1] += 40;
    const moodShift = { happy: 40, motivating: 80, success: 120, encouraging: 160 }[mood] ?? 0; color[1] = (color[1] + moodShift) % 256;
    counter++; color[0] = (color[0] + counter * 23) % 256; color[2] = (color[2] + counter * 11) % 256;   // jede Antwort ist ein anderes Bild (wie bei einem echten Modell)
    res.setHeader('Content-Type', 'application/json');
    res.end(JSON.stringify({ data: [{ b64_json: png(256, 256, color).toString('base64') }] }));
  });
}).listen(port, '127.0.0.1', () => console.log('Bild-Stub auf ' + port));
