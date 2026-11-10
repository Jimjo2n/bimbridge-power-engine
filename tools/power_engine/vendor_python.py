# SPDX-License-Identifier: GPL-3.0-only
"""Arma la carpeta del Python Document Worker para dist/: código del worker + dependencias puras verificadas por SHA-256.

uso: python3 -I tools/power_engine/vendor_python.py <destino>

- Sólo ruedas py3-none-any (sin binarios de una plataforma concreta).
- 'cryptography' no se empaqueta (tiene binarios): se instala un sustituto que sólo falla si un PDF usa cifrado AES; si el equipo
  tiene la biblioteca real, la del sistema gana (vendor va al FINAL de sys.path).
"""
import hashlib
import os
import shutil
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SRC = os.path.join(ROOT, "workers", "python")
WHEELS = os.path.join(SRC, "wheels")

STUB = '''"""Sustituto empaquetado de cryptography: el worker no trae binarios. Sólo los PDF con cifrado AES lo necesitan."""


class _NoCrypto(object):
    def __init__(self, *a, **k):
        raise NotImplementedError("PDF con cifrado AES: no soportado por el Python Worker empaquetado (instale 'cryptography' en el equipo)")

    def __getattr__(self, name):
        raise NotImplementedError("PDF con cifrado AES: no soportado por el Python Worker empaquetado")


def default_backend():
    return None


Cipher = _NoCrypto
algorithms = _NoCrypto
modes = _NoCrypto
'''


def sha256(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def main(dest):
    if os.path.exists(dest):
        shutil.rmtree(dest)
    shutil.copytree(os.path.join(SRC, "bimbridge_docworker"), os.path.join(dest, "bimbridge_docworker"),
                    ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    shutil.copy2(os.path.join(SRC, "run_worker.py"), dest)
    vendor = os.path.join(dest, "vendor")
    os.makedirs(vendor)
    for line in open(os.path.join(WHEELS, "LOCK.txt"), encoding="utf-8"):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        name, want = line.split()
        p = os.path.join(WHEELS, name)
        got = sha256(p)
        if got != want:
            sys.exit("hash distinto en %s: %s (esperado %s)" % (name, got, want))
        if not name.endswith("-py3-none-any.whl"):
            sys.exit("sólo ruedas puras: " + name)
        with zipfile.ZipFile(p) as z:
            for m in z.namelist():
                if ".dist-info/" in m and not m.endswith(("LICENSE", "METADATA")) and "licenses/" not in m:
                    continue
                target = os.path.realpath(os.path.join(vendor, m))
                if not target.startswith(os.path.realpath(vendor) + os.sep):
                    sys.exit("entrada fuera del destino en " + name + ": " + m)
                z.extract(m, vendor)
    pkg = os.path.join(vendor, "cryptography")
    for sub in ("", "hazmat", os.path.join("hazmat", "backends"), os.path.join("hazmat", "primitives"), os.path.join("hazmat", "primitives", "ciphers")):
        os.makedirs(os.path.join(pkg, sub), exist_ok=True)
        with open(os.path.join(pkg, sub, "__init__.py"), "w", encoding="utf-8") as f:
            f.write(STUB if sub.endswith("backends") or sub.endswith("ciphers") else '"""sustituto empaquetado (ver hazmat)"""\n')
    print("python worker -> " + dest)


if __name__ == "__main__":
    main(os.path.abspath(sys.argv[1]))
