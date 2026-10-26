"""Unabhängige Prüfung der Learny-PKI (ADR 0035) mit OpenSSL und pyHanko — ohne Learny-Code.

Eingabe: Verzeichnis mit pki-root.cer, pki-issuing.cer, pki-signing.cer, pki-root.crl, pki-issuing.crl und pki-signed.pdf
(erzeugt vom Integrationstest mit TEST_DUMP_DIR=<verzeichnis>). Aufruf: python tools/pki/validate_pki.py <verzeichnis>
"""
import os, subprocess, sys

d = sys.argv[1]
P = lambda n: os.path.join(d, n)
fails = []

def run(*a):
    r = subprocess.run(a, capture_output=True, text=True)
    return r.returncode, (r.stdout + r.stderr)

def check(ok, name, detail=""):
    print(("OK   " if ok else "FAIL ") + name + (f" – {detail}" if detail and not ok else ""), flush=True)
    if not ok: fails.append(name)

rc, v = run("openssl", "version"); print(v.strip())
for n in ("root", "issuing", "signing"):
    run("openssl", "x509", "-inform", "der", "-in", P(f"pki-{n}.cer"), "-out", P(f"{n}.pem"))
def text(n): return run("openssl", "x509", "-in", P(f"{n}.pem"), "-noout", "-text")[1]
tr, ti, ts = text("root"), text("issuing"), text("signing")
check("CA:TRUE, pathlen:1" in tr and "Certificate Sign, CRL Sign" in tr and "Subject Key Identifier" in tr, "OpenSSL: Root-Erweiterungen")
check("CA:TRUE, pathlen:0" in ti and "Certificate Sign, CRL Sign" in ti and "Authority Key Identifier" in ti and "CRL Distribution Points" in ti, "OpenSSL: Issuing-Erweiterungen")
check("CA:FALSE" in ts and "Digital Signature" in ts and "1.3.6.1.5.5.7.3.36" in ts and "CRL Distribution Points" in ts and "OCSP" not in ts, "OpenSSL: Signaturzertifikat (EKU documentSigning, CA:FALSE, keine OCSP-URL)")
rc, o = run("openssl", "verify", "-CAfile", P("root.pem"), "-untrusted", P("issuing.pem"), P("signing.pem")); check(rc == 0, "OpenSSL: Kette Signatur → Issuing → Root", o)
for n, ca in (("root", "root"), ("issuing", "issuing")):
    rc, o = run("openssl", "crl", "-inform", "der", "-in", P(f"pki-{n}.crl"), "-CAfile", P(f"{ca}.pem"), "-noout", "-verify"); check(rc == 0 and "verify OK" in o, f"OpenSSL: {n}-CRL Signatur", o)
    rc, o = run("openssl", "crl", "-inform", "der", "-in", P(f"pki-{n}.crl"), "-noout", "-text"); check("CRL Number" in o and "Next Update" in o and "Authority Key Identifier" in o, f"OpenSSL: {n}-CRL (Nummer, nextUpdate, AKI)")
bundle = open(P("root.pem")).read() + "".join(run("openssl", "crl", "-inform", "der", "-in", P(f"pki-{n}.crl"), "-outform", "pem")[1] for n in ("root", "issuing"))
open(P("bundle.pem"), "w").write(bundle)
rc, o = run("openssl", "verify", "-crl_check_all", "-CAfile", P("bundle.pem"), "-untrusted", P("issuing.pem"), P("signing.pem")); check(rc == 0, "OpenSSL: Kette mit CRL-Prüfung (alle Ebenen)", o)

# pyHanko: PDF-Signatur und Vertrauenskette gegen die Learny-Root als einzigen Anker
try:
    import pyhanko, io
    from pyhanko.pdf_utils.reader import PdfFileReader
    from pyhanko.sign.validation import validate_pdf_signature
    from pyhanko_certvalidator import ValidationContext
    from pyhanko.keys import load_cert_from_pemder
    import importlib.metadata as md; print("pyHanko", md.version("pyHanko"))
    root = load_cert_from_pemder(P("pki-root.cer"))
    with open(P("pki-signed.pdf"), "rb") as f:
        r = PdfFileReader(f)
        sigs = r.embedded_signatures
        check(len(sigs) == 1, "pyHanko: genau eine eingebettete Signatur")
        vc = ValidationContext(trust_roots=[root], allow_fetching=False)
        st = validate_pdf_signature(sigs[0], vc)
        check(st.intact and st.valid, "pyHanko: Signatur gültig und Dokument unverändert", str(st.summary()))
        check(st.trusted, "pyHanko: Kette bis zur Learny-Root vertrauenswürdig (Root als alleiniger Vertrauensanker)", str(st.summary()))
        vc2 = ValidationContext(trust_roots=[], allow_fetching=False)
        try: untrusted = not validate_pdf_signature(sigs[0], vc2).trusted
        except Exception: untrusted = True   # pyHanko meldet „kein Pfad zum Vertrauensanker“ als Fehler
        check(untrusted, "pyHanko: ohne Learny-Root als Anker wird die Signatur NICHT als vertrauenswürdig geführt (keine öffentliche Anerkennung)")
except ImportError as e:
    print("pyHanko nicht installiert – übersprungen:", e)
sys.exit(1 if fails else 0)
