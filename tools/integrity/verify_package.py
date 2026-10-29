#!/usr/bin/env python3
"""Unabhängige Prüfung eines Learny-Zertifikatspakets ohne Learny-Backend (ADR 0039).

Eingabe: ein Verzeichnis mit den aus dem PDF extrahierten Anhängen (z. B. `pdfdetach -saveall zertifikat.pdf -o anhaenge/`
oder `qpdf --show-attachment` / Acrobat „Anlagen speichern“). Nur Python-Standardbibliothek (ES256 wird mit eigener
P-256-Arithmetik geprüft). Die PDF-Signatur (CMS/CAdES) prüft dieses Skript NICHT — dafür Adobe Acrobat, `pdfsig`
(poppler) oder die Learny-Prüfseite; die Vertrauenskette des PDF-Unterzeichners ist ein getrennter Schritt.

Aufruf:
  python verify_package.py ANHAENGE_DIR [--trusted-thumbprint T] [--jwks-url URL] [--bitcoin-rpc URL --rpc-user U --rpc-pass P]

Ausgabe: getrennte Statuszeilen; der Exit-Code ist 0 nur, wenn Manifest, JWS, Dateihashes und alle Credential-JWT gültig
sind UND — falls vorhanden — die OTS-Datei zur Manifest-JWS gehört. Die Bitcoin-Verankerung wird nur mit eigenem Knoten
(`--bitcoin-rpc`) als „geprüft“ ausgewiesen; sonst steht dort „nicht überprüfbar“.
"""
import argparse, base64, hashlib, json, os, sys, urllib.request

P = 0xFFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF
N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
B = 0x5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B
G = (0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296, 0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5)


def b64d(s):
    return base64.urlsafe_b64decode(s + "=" * (-len(s) % 4))


def add(p1, p2):
    if p1 is None: return p2
    if p2 is None: return p1
    (x1, y1), (x2, y2) = p1, p2
    if x1 == x2 and (y1 + y2) % P == 0: return None
    m = ((3 * x1 * x1 - 3) * pow(2 * y1, -1, P) if p1 == p2 else (y2 - y1) * pow(x2 - x1, -1, P)) % P
    x3 = (m * m - x1 - x2) % P
    return (x3, (m * (x1 - x3) - y1) % P)


def mul(k, pt):
    r = None
    while k:
        if k & 1: r = add(r, pt)
        pt = add(pt, pt); k >>= 1
    return r


def es256_verify(signing_input: bytes, sig: bytes, jwk: dict) -> bool:
    if len(sig) != 64 or jwk.get("kty") != "EC" or jwk.get("crv") != "P-256": return False
    q = (int.from_bytes(b64d(jwk["x"]), "big"), int.from_bytes(b64d(jwk["y"]), "big"))
    if (q[1] * q[1] - (q[0] ** 3 - 3 * q[0] + B)) % P != 0: return False   # Punkt liegt nicht auf der Kurve
    r, s = int.from_bytes(sig[:32], "big"), int.from_bytes(sig[32:], "big")
    if not (0 < r < N and 0 < s < N): return False
    z = int.from_bytes(hashlib.sha256(signing_input).digest(), "big")
    w = pow(s, -1, N)
    pt = add(mul(z * w % N, G), mul(r * w % N, q))
    return pt is not None and pt[0] % N == r


def thumbprint(jwk):
    j = '{"crv":"%s","kty":"%s","x":"%s","y":"%s"}' % (jwk["crv"], jwk["kty"], jwk["x"], jwk["y"])
    return base64.urlsafe_b64encode(hashlib.sha256(j.encode()).digest()).rstrip(b"=").decode()


def parse_jws(jws: str):
    h, p, s = jws.strip().split(".")
    return json.loads(b64d(h)), b64d(p), b64d(s), (h + "." + p).encode("ascii")


def ots_parse(data: bytes):
    magic = b"\x00OpenTimestamps\x00\x00Proof\x00\xbf\x89\xe2\xe8\x84\xe8\x92\x94"
    if not data.startswith(magic): raise ValueError("keine OTS-Datei")
    pos = [len(magic)]
    def byte():
        pos[0] += 1; return data[pos[0] - 1]
    def varuint():
        v = sh = 0
        while True:
            b = byte(); v |= (b & 0x7F) << sh; sh += 7
            if not b & 0x80: return v
    def varbytes():
        n = varuint(); out = data[pos[0]:pos[0] + n]; pos[0] += n; return out
    if varuint() != 1: raise ValueError("OTS-Version")
    tag = byte(); ln = {0x08: 32, 0x02: 20, 0x03: 20}[tag]
    digest = data[pos[0]:pos[0] + ln]; pos[0] += ln
    atts = []
    def stamp(msg):
        t = byte()
        while t == 0xFF:
            item(byte(), msg); t = byte()
        item(t, msg)
    def item(t, msg):
        if t == 0x00:
            kind = data[pos[0]:pos[0] + 8]; pos[0] += 8; payload = varbytes()
            if kind == bytes.fromhex("0588960d73d71901"):
                v = sh = 0
                for b in payload:
                    v |= (b & 0x7F) << sh; sh += 7
                atts.append(("bitcoin", v, msg))
            elif kind == bytes.fromhex("83dfe30d2ef90c8e"): atts.append(("pending", None, msg))
            else: atts.append(("unknown", None, msg))
            return
        if t == 0x08: nxt = hashlib.sha256(msg).digest()
        elif t == 0x03: nxt = hashlib.new("ripemd160", msg).digest()   # ältere Receipts (z. B. Beispieldateien); benötigt OpenSSL mit RIPEMD-160
        elif t == 0x02: nxt = hashlib.sha1(msg).digest()
        elif t == 0xF0: nxt = msg + varbytes()
        elif t == 0xF1: nxt = varbytes() + msg
        elif t == 0xF2: nxt = msg[::-1]
        elif t == 0xF3: nxt = msg.hex().encode()
        else: raise ValueError("unbekannte Operation 0x%02x" % t)
        stamp(nxt)
    stamp(digest)
    return tag, digest, atts


def rpc(url, user, pw, method, params):
    req = urllib.request.Request(url, json.dumps({"jsonrpc": "1.0", "id": "v", "method": method, "params": params}).encode(), {"Content-Type": "application/json"})
    if user: req.add_header("Authorization", "Basic " + base64.b64encode(f"{user}:{pw}".encode()).decode())
    with urllib.request.urlopen(req, timeout=20) as r:
        out = json.loads(r.read())
    if out.get("error"): raise RuntimeError(out["error"])
    return out["result"]


def explorer_header(base, height):
    """Block-Header über eine Esplora-kompatible API (z. B. mempool.space, blockstream.info) — ZUSATZANNAHME: Betreiber des Explorers.
    Der Header wird selbst geprüft (Doppel-SHA-256 = Blockhash, Proof-of-Work gegen das Ziel aus nBits); Höhe/Tiefe und Kettenzugehörigkeit stammen vom Explorer."""
    get = lambda path: urllib.request.urlopen(urllib.request.Request(base.rstrip("/") + path, headers={"User-Agent": "learny-verify"}), timeout=30).read().decode().strip()
    bhash = get("/api/block-height/%d" % height)
    raw = bytes.fromhex(get("/api/block/%s/header" % bhash))
    if len(raw) != 80: raise ValueError("Header hat nicht 80 Byte")
    calc = hashlib.sha256(hashlib.sha256(raw).digest()).digest()[::-1].hex()
    if calc != bhash: raise ValueError("Header passt nicht zum Blockhash")
    bits = int.from_bytes(raw[72:76], "little")
    target = (bits & 0xFFFFFF) * 2 ** (8 * ((bits >> 24) - 3))
    if int(calc, 16) > target: raise ValueError("Proof-of-Work ungültig")
    tip = int(get("/api/blocks/tip/height"))
    return {"hash": bhash, "merkle_internal": raw[36:68], "time": int.from_bytes(raw[68:72], "little"), "tip": tip}


def ots_check(file_path, ots_path, explorer, confirmations):
    data = open(file_path, "rb").read(); tag, digest, atts = ots_parse(open(ots_path, "rb").read())
    print("Datei-SHA-256        :", hashlib.sha256(data).hexdigest())
    print("Digest in der Receipt:", digest.hex(), "(Operation %s)" % ("sha256" if tag == 8 else hex(tag)))
    print("Bindung              :", "GÜLTIG" if tag == 8 and digest == hashlib.sha256(data).digest() else "UNGÜLTIG")
    for kind, h, msg in atts:
        if kind == "pending": print("Attestierung         : pending (Calendar) – Merkle-Pfad bis", msg.hex()); continue
        if kind != "bitcoin": print("Attestierung         :", kind); continue
        print("Attestierung         : Bitcoin-Block", h, "– berechneter Wert", msg.hex())
        if not explorer: print("Bitcoin-Prüfung      : NICHT ÜBERPRÜFBAR (--explorer oder eigener Knoten fehlt)"); continue
        hd = explorer_header(explorer, h)
        same = hd["merkle_internal"] == msg
        print("Blockhash            :", hd["hash"]); print("Blockzeit (UTC)      :", __import__("datetime").datetime.fromtimestamp(hd["time"], __import__("datetime").timezone.utc).isoformat())
        print("Merkle-Root          :", "GÜLTIG (stimmt mit dem Block überein)" if same else "UNGÜLTIG"); d = hd["tip"] - h + 1
        print("Bestätigungstiefe    : %d (%s)" % (d, "ausreichend" if d >= confirmations else "nicht ausreichend"))
        print("Vertrauensgrundlage  : PoW-Header selbst geprüft; Kettenzugehörigkeit/Tiefe laut", explorer)


def main():
    sys.stdout.reconfigure(encoding="utf-8")   # Windows-Konsolen (cp1252) verschlucken sonst Umlaute und Symbole
    if len(sys.argv) > 1 and sys.argv[1] == "ots":   # Einzelprüfung: verify_package.py ots DATEI DATEI.ots [--explorer URL]
        sys.stdout.reconfigure(encoding="utf-8"); ex = sys.argv[sys.argv.index("--explorer") + 1] if "--explorer" in sys.argv else None
        ots_check(sys.argv[2], sys.argv[3], ex, 6); return
    ap = argparse.ArgumentParser()
    ap.add_argument("dir"); ap.add_argument("--trusted-thumbprint"); ap.add_argument("--jwks-url")
    ap.add_argument("--bitcoin-rpc"); ap.add_argument("--rpc-user", default=""); ap.add_argument("--rpc-pass", default=""); ap.add_argument("--confirmations", type=int, default=6)
    a = ap.parse_args()
    read = lambda n: open(os.path.join(a.dir, n), "rb").read()
    exists = lambda n: os.path.exists(os.path.join(a.dir, n))
    ok_all = True
    def report(name, state, detail=""):
        nonlocal ok_all
        print(f"{name:<42} {state}{(' – ' + detail) if detail else ''}")
        if state in ("UNGÜLTIG",): ok_all = False

    inv = sorted(f for f in os.listdir(a.dir) if os.path.isfile(os.path.join(a.dir, f)))
    jwk = json.loads(read("public-key.jwk")) if exists("public-key.jwk") else None
    if not (exists("integrity-manifest.json") and exists("integrity-manifest.jws")):
        report("Manifest", "NICHT VORHANDEN", "älteres Paket – nur verification.json und PDF-Signatur prüfbar"); ok_all = False
    else:
        mbytes, jws = read("integrity-manifest.json"), read("integrity-manifest.jws")
        manifest = json.loads(mbytes)
        hdr, payload, sig, sin = parse_jws(jws.decode("ascii"))
        report("Manifest-JWS (ES256)", "GÜLTIG" if jwk and es256_verify(sin, sig, jwk) and hdr.get("alg") == "ES256" and hdr.get("typ") == "learny-integrity-manifest+jws" else "UNGÜLTIG")
        report("JWS-Nutzlast = Manifestbytes", "GÜLTIG" if payload == mbytes else "UNGÜLTIG")
        tp = thumbprint(jwk) if jwk else None
        report("JWK ↔ Manifest-Thumbprint", "GÜLTIG" if tp and tp == manifest["issuer_key"]["jwk_thumbprint"] else "UNGÜLTIG")
        # Ausstellervertrauen: nie aus dem Paket selbst
        if a.trusted_thumbprint: report("Ausstellerschlüssel (Vertrauensanker)", "BESTÄTIGT" if tp == a.trusted_thumbprint else "UNGÜLTIG", "Thumbprint stimmt mit dem vorgegebenen Anker überein" if tp == a.trusted_thumbprint else "weicht vom vorgegebenen Anker ab")
        elif a.jwks_url:
            try:
                keys = json.loads(urllib.request.urlopen(urllib.request.Request(a.jwks_url, headers={"User-Agent": "learny-verify"}), timeout=20).read())["keys"]   # manche Proxys (Cloudflare) sperren den Standard-User-Agent von urllib
                hit = any(thumbprint(k) == tp for k in keys)
                report("Ausstellerschlüssel (JWKS online)", "BESTÄTIGT" if hit else "UNGÜLTIG", "Zusatzannahme: TLS/DNS der Aussteller-Domain")
            except Exception as e:
                report("Ausstellerschlüssel (JWKS online)", "NICHT ÜBERPRÜFBAR", type(e).__name__)
        else: report("Ausstellerschlüssel", "NICHT BESTÄTIGT", "mathematisch geprüft; Echtheit nur mit --trusted-thumbprint oder --jwks-url")
        listed = {f["name"]: f for f in manifest["files"]}
        bad = [n for n, f in listed.items() if not exists(n) or hashlib.sha256(read(n)).hexdigest() != f["sha256"] or len(read(n)) != f["size"]]
        report("Dateihashes (alle Nutzdateien)", "UNGÜLTIG" if bad else "GÜLTIG", ", ".join(bad))
        extra = [f for f in inv if f not in listed and f not in ("integrity-manifest.json", "integrity-manifest.jws", "integrity-manifest.jws.ots")]
        report("Zusätzliche/versteckte Anhänge", "UNGÜLTIG" if extra else "KEINE", ", ".join(extra))
        missing = [n for n in manifest["rules"]["required_files"] if n not in listed]
        report("Pflichtdateien", "UNGÜLTIG" if missing else "VOLLSTÄNDIG", ", ".join(missing))
        # Credential-JWT einzeln
        for f in [n for n in inv if n.endswith(".jwt")]:
            h, pl, sg, si = parse_jws(read(f).decode("ascii"))
            vc = json.loads(pl)
            good = jwk and es256_verify(si, sg, jwk) and h.get("alg") == "ES256" and h.get("typ") == "JWT" and all(k in json.loads(pl) for k in ("iss", "jti", "nbf", "sub", "vc"))
            report(f"Credential-JWT {f}", "GÜLTIG" if good else "UNGÜLTIG", "iss=" + str(json.loads(pl).get("iss")))
            for emb in (vc.get("credentialSubject", {}).get("verifiableCredential") or []):
                if isinstance(emb, str):
                    eh, ep, es, ei = parse_jws(emb)
                    report("  eingebettetes Credential", "GÜLTIG" if jwk and es256_verify(ei, es, jwk) else "UNGÜLTIG", json.loads(ep).get("jti", ""))
        # OpenTimestamps
        if exists("integrity-manifest.jws.ots"):
            try:
                tag, digest, atts = ots_parse(read("integrity-manifest.jws.ots"))
                bound = tag == 0x08 and digest == hashlib.sha256(jws).digest()
                report("OTS-Receipt ↔ Manifest-JWS", "GÜLTIG" if bound else "UNGÜLTIG", "SHA-256 über die exakten Bytes von integrity-manifest.jws")
                btc = [x for x in atts if x[0] == "bitcoin"]
                if not btc: report("Bitcoin-Verankerung", "AUSSTEHEND", "nur Calendar-Attestierung (ots upgrade später ausführen)")
                elif not a.bitcoin_rpc: report("Bitcoin-Verankerung", "NICHT ÜBERPRÜFBAR", f"Höhe {btc[0][1]} behauptet; eigenen Knoten mit --bitcoin-rpc angeben")
                else:
                    kind, height, msg = btc[0]
                    try:
                        h = rpc(a.bitcoin_rpc, a.rpc_user, a.rpc_pass, "getblockheader", [rpc(a.bitcoin_rpc, a.rpc_user, a.rpc_pass, "getblockhash", [height])])
                        tip = rpc(a.bitcoin_rpc, a.rpc_user, a.rpc_pass, "getblockcount", [])
                        same = bytes.fromhex(h["merkleroot"])[::-1] == msg
                        depth = tip - height + 1
                        report("Bitcoin-Verankerung", "GÜLTIG" if same else "UNGÜLTIG", f"Block {height} {h['hash']} Zeit {h['time']}")
                        report("Bestätigungstiefe", f"{depth} Blöcke ({'ausreichend' if depth >= a.confirmations else 'noch nicht ausreichend'})")
                    except Exception as e:
                        report("Bitcoin-Verankerung", "NICHT ÜBERPRÜFBAR", type(e).__name__)
            except Exception as e:
                report("OTS-Receipt", "UNGÜLTIG", str(e))
        else: report("OTS-Receipt", "NICHT VORHANDEN", "kein Zeitnachweis eingebettet")
    print("\nNicht Gegenstand dieses Skripts: PDF-CMS-Signatur und Zertifikatskette, Widerruf (https://…/api/public/revocation-list), 1EdTech-Validierung.")
    print("Hinweis: Ein Zeitnachweis bestätigt weder Ausstelleridentität noch Richtigkeit des Lernnachweises noch den Widerrufsstatus.")
    sys.exit(0 if ok_all else 1)


if __name__ == "__main__":
    main()
