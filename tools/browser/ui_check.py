"""Browserprüfung (Playwright, Chromium headless): legt per API einen vollständigen Themenabschluss an und prüft Nachweisübersicht, LinkedIn-Panel, PDF-Download,
Upload-Prüfung (gültig/manipuliert), öffentliche Prüfseite (mobil, dunkel). Voraussetzung: gebaute Web-App, `pip install playwright`, `python -m playwright install chromium`.
Aufruf: PYTHONIOENCODING=utf-8 python tools/browser/ui_check.py   (startet eine eigene Entwicklungsinstanz mit temporärer Datenbank)
"""
import json, os, subprocess, sys, tempfile, time, urllib.request, http.cookiejar, urllib.parse, shutil
from playwright.sync_api import sync_playwright

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))  # Repository-Wurzel
TMP = tempfile.mkdtemp(prefix="lc-ui-")
PORT = 5091
BASE = f"http://127.0.0.1:{PORT}"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ui")
os.makedirs(OUT, exist_ok=True)

env = dict(os.environ, ASPNETCORE_URLS=BASE, ASPNETCORE_ENVIRONMENT="Development", ASPNETCORE_CONTENTROOT=os.path.join(ROOT, "src", "LearnCards.Web"),
           AUTH_MODE="dev", MCP_API_KEY="k", MCP_TOKEN_KEY="aW50ZWdyYXRpb24tdGVzdC1rZXktMDEyMzQ1Njc4OSE=", OPENAI_API_KEY="", DB_PROVIDER="sqlite",
           SQLITE_PATH=os.path.join(TMP, "ui.db"), DATA_PROTECTION_DIR=os.path.join(TMP, "keys"), BADGE_SIGNING_KEY_FILE=os.path.join(TMP, "badge.pem"))
dll = os.path.join(ROOT, "src", "LearnCards.Web", "bin", "Debug", "net10.0", "LearnCards.Web.dll")
log = open(os.path.join(TMP, "web.log"), "w")
proc = subprocess.Popen(["dotnet", dll], cwd=TMP, env=env, stdout=log, stderr=subprocess.STDOUT)
results = []
def check(ok, name):
    results.append((ok, name)); print(("OK   " if ok else "FAIL ") + name, flush=True)

def api(opener, method, path, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, method=method, headers={"Content-Type": "application/json"})
    try:
        with opener.open(req) as r:
            t = r.read().decode()
            return r.status, (json.loads(t) if t.startswith(("{", "[")) else t)
    except urllib.error.HTTPError as e:
        t = e.read().decode()
        return e.code, (json.loads(t) if t.startswith(("{", "[")) else t)

def login(user):
    cj = http.cookiejar.CookieJar()
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *a, **k): return None
    op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj), NoRedirect)
    try: op.open(BASE + "/auth/login?devUser=" + user)
    except urllib.error.HTTPError: pass
    return op, cj

try:
    for _ in range(80):
        try:
            urllib.request.urlopen(BASE + "/health"); break
        except Exception: time.sleep(0.5)
    owner, _ = login("ui-owner"); lea, lea_cj = login("ui-lea")
    lea_id = api(lea, "GET", "/api/me")[1]["id"]
    def module(name, comps):
        return {"format": "learny-module", "schema_version": 1, "name": name,
                "learning_objectives": [{"id": "o1", "text": "Grundlagen"}] if comps else [],
                "competencies": [{"id": "k1", "objective_id": "o1", "name": "Grundlagen anwenden", "description": "", "pass_percent": 60, "min_questions": 1}] if comps else [],
                "cards": [{"category": "K", "term": f"{name}-{i}", "question": f"Frage {name} {i}?", "definition": "D", "competencies": ["k1"] if comps else [],
                           "quiz": [{"type": "single_choice", "question": f"Quiz {name} {i}?", "options": ["falsch1", "richtig", "falsch2", "falsch3"], "correct_index": 1, "explanation": "w"}]} for i in (1, 2, 3)]}
    api(owner, "POST", "/api/modules/import", module("UI-A", True)); api(owner, "POST", "/api/modules/import", module("UI-B", False))
    topic = {"format": "learny-topic", "schema_version": 1, "name": "UI Thema", "description": "d",
             "modules": [{"module_name": "UI-A", "exam": {"enabled": True, "question_count": 2, "pass_percent": 60}}, {"module_name": "UI-B", "exam": {"enabled": True, "question_count": 2, "pass_percent": 60}}],
             "exam_config": {"final_exam": {"enabled": True, "question_count": 3, "pass_percent": 60, "unlock": {"require_all_modules_completed": False, "require_module_exams_passed": True}}}}
    tid = api(owner, "POST", "/api/topics/import", topic)[1]["id"]
    api(owner, "POST", f"/api/topics/{tid}/versions", {}); api(owner, "POST", f"/api/topics/{tid}/shares", {"user_id": lea_id})
    detail = api(lea, "GET", f"/api/topics/{tid}?version=1")[1]
    frozen = {m["name"]: m["module_id"] for m in detail["modules"]}
    def pass_exam(kind, module_id):
        s, start = api(lea, "POST", f"/api/topics/{tid}/exams/{kind}", {"module_id": module_id} if module_id else {})
        answers = [{"question_id": q["question_id"], "selected_indexes": [max(0, q["options"].index("richtig"))]} for q in start["questions"]]
        return api(lea, "POST", "/api/quiz/submit", {"module_id": start["module_id"], "session_id": start["session_id"], "answers": answers})[1]
    pass_exam("module", frozen["UI-A"]); pass_exam("module", frozen["UI-B"])
    for mid in frozen.values():
        for c in api(lea, "GET", f"/api/cards/?module_id={mid}")[1]:
            api(lea, "PUT", f"/api/cards/{c['id']}/state", {"is_checked": True, "marked_review": False})
    pass_exam("final", None)
    certs = api(lea, "GET", "/api/certificates/")[1]
    check(any(c["kind"] == "topic" for c in certs), f"Ausgangslage: Themenabschluss ausgestellt ({len(certs)} Zertifikate)")

    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        ctx = browser.new_context(viewport={"width": 1280, "height": 900}, accept_downloads=True)
        page = ctx.new_page(); errors = []
        page.on("pageerror", lambda e: errors.append(str(e)))
        page.on("console", lambda m: errors.append(m.text) if m.type == "error" else None)
        page.goto(BASE + "/auth/login?devUser=ui-lea"); page.wait_for_load_state("networkidle")
        page.goto(BASE + "/achievements"); page.wait_for_load_state("networkidle")
        page.wait_for_selector("text=Meine Abschlüsse und Nachweise")
        check(page.locator("article").count() == 3, f"Nachweisübersicht zeigt 3 Abschlüsse (2 Module + 1 Thema): {page.locator('article').count()}")
        check(page.locator("text=Themenabschluss · Master-Badge + CLR").count() == 1 and page.locator("text=Grundlagen anwenden").count() >= 1, "Themen- und Modulabschlüsse mit Kompetenz sichtbar")
        check(page.locator('img[alt^="Learny Verified"]').first.evaluate("e => e.complete && e.naturalWidth > 0"), "Prüfsiegel (SVG) wird tatsächlich geladen")
        check(page.locator("header .seal-link img").evaluate("e => e.complete && e.naturalWidth > 0"), "Siegel im Seitenkopf geladen")
        topic_card = page.locator("article", has_text="Themenabschluss · Master-Badge + CLR")
        topic_card.get_by_role("button", name="Auf LinkedIn hinzufügen").click()
        page.wait_for_selector("text=Es wird nichts automatisch veröffentlicht")
        li_href = topic_card.get_by_role("link", name="LinkedIn öffnen").get_attribute("href")
        check(li_href.startswith("https://www.linkedin.com/profile/add") and "certUrl=" in li_href, "LinkedIn-Panel öffnet sich per Klick (Blazor-Interaktivität), Link erst nach Klick")
        check(topic_card.get_by_role("link", name="LinkedIn öffnen").get_attribute("rel") == "noopener noreferrer" and topic_card.get_by_role("link", name="LinkedIn öffnen").get_attribute("target") == "_blank", "LinkedIn-Link: neuer Tab, noopener noreferrer")
        page.screenshot(path=os.path.join(OUT, "achievements.png"), full_page=True)
        with page.expect_download() as d:
            topic_card.get_by_role("link", name="Themenabschluss als PDF").click()
        pdf_path = os.path.join(OUT, "download.pdf"); d.value.save_as(pdf_path)
        pdf = open(pdf_path, "rb").read()
        check(pdf[:5] == b"%PDF-" and len(pdf) > 5000, f"PDF-Download im Browser: {len(pdf)} Bytes")
        # Prüfseite mit Upload
        page.goto(BASE + "/verify"); page.wait_for_load_state("networkidle")
        page.set_input_files('input[type=file]', pdf_path)
        page.wait_for_selector("text=Prüfpunkt", timeout=30000)
        txt = page.inner_text("body")
        check("✔ Gültig" in txt or "Gültig:" in txt or "Technisch gültig" in txt, "Upload-Prüfung: Gesamtergebnis gültig")
        check(txt.count("✔") >= 7 and "✖" not in txt.split("Prüfpunkt", 1)[1], f"alle neun Prüfpunkte positiv (✔: {txt.count('✔')})")
        page.screenshot(path=os.path.join(OUT, "verify-upload.png"), full_page=True)
        # manipuliertes PDF
        bad = bytearray(pdf); bad[len(bad) // 3] ^= 0x55
        bad_path = os.path.join(OUT, "bad.pdf"); open(bad_path, "wb").write(bad)
        page.goto(BASE + "/verify"); page.wait_for_load_state("networkidle")
        page.set_input_files('input[type=file]', bad_path)
        page.wait_for_selector("text=Prüfpunkt", timeout=30000)
        txt2 = page.inner_text("body")
        check("Nicht gültig" in txt2 and "✖" in txt2, "Upload-Prüfung: manipuliertes PDF wird als nicht gültig angezeigt")
        # öffentliche Prüfseite (ohne Anmeldung) und Dunkelmodus
        cert_id = [c for c in certs if c["kind"] == "topic"][0]["id"]
        anon = browser.new_context(color_scheme="dark", viewport={"width": 390, "height": 800}).new_page()
        anon.goto(BASE + f"/verify/{cert_id}"); anon.wait_for_load_state("networkidle")
        t = anon.inner_text("body")
        check("Nachweise und Kompetenzen" in t and "UI-A" in t and "ui-lea" not in t.lower(), "Öffentliche Prüfseite (mobil, dunkel): Kompetenzen sichtbar, kein Lernendenname")
        check(anon.evaluate("document.documentElement.scrollWidth <= window.innerWidth + 1"), "Mobilansicht ohne horizontales Scrollen")
        anon.screenshot(path=os.path.join(OUT, "verify-mobile-dark.png"), full_page=True)
        check(not [e for e in errors if "favicon" not in e.lower()], f"keine Konsolen-/Seitenfehler im Browser ({len(errors)})")
        browser.close()
finally:
    proc.terminate()
    try: proc.wait(10)
    except Exception: proc.kill()
    log.close()
fails = [n for ok, n in results if not ok]
print(f"\n{len(results) - len(fails)}/{len(results)} Browserprüfungen bestanden")
sys.exit(1 if fails else 0)
