# SPDX-License-Identifier: GPL-3.0-only
"""Bucle del Python Worker: contrato, guardas (rutas, red, memoria) y despacho de trabajos."""
import hashlib
import json
import os
import socket
import sys
import time
import traceback

from . import __version__, PROTOCOL

_HERE = os.path.dirname(os.path.abspath(__file__))
# dependencias puras empaquetadas (pdfminer.six, charset_normalizer) AL FINAL: las del sistema ganan si existen
_VENDOR = os.path.join(os.path.dirname(_HERE), "vendor")
if os.path.isdir(_VENDOR) and _VENDOR not in sys.path:
    sys.path.append(_VENDOR)


class JobError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code
        self.message = message


# ---------------- guardas ----------------

def _no_network(*_a, **_k):
    raise JobError("RUTA_DENEGADA", "el Python Worker no tiene acceso a la red")


def block_network():
    """Sin red por defecto: ningún documento sale del equipo por accidente (ni por una biblioteca)."""
    socket.socket = _no_network  # type: ignore
    socket.create_connection = _no_network  # type: ignore
    socket.getaddrinfo = _no_network  # type: ignore


_mem_limit_set = [0]


def limit_memory(mb):
    """Límite de memoria del proceso (POSIX). En Windows lo vigila el núcleo C# (mata el proceso si lo supera)."""
    if not mb or mb <= 0 or mb == _mem_limit_set[0]:
        return
    try:
        import resource
        b = int(mb) * 1024 * 1024
        soft, hard = resource.getrlimit(resource.RLIMIT_AS)
        if hard != resource.RLIM_INFINITY:
            b = min(b, hard)
        resource.setrlimit(resource.RLIMIT_AS, (b, hard))
        _mem_limit_set[0] = mb
    except (ImportError, ValueError, OSError):
        pass


class Paths:
    """Rutas autorizadas para UN trabajo. Se compara la ruta real (sin enlaces) con prefijos reales."""

    def __init__(self, allowed):
        self.allowed = [os.path.realpath(p) for p in (allowed or []) if p]

    def check(self, path):
        if not path or not isinstance(path, str):
            raise JobError("INVALIDO", "falta la ruta del documento")
        real = os.path.realpath(path)
        for a in self.allowed:
            if os.path.normcase(real) == os.path.normcase(a) or os.path.normcase(real).startswith(os.path.normcase(a.rstrip(os.sep) + os.sep)):
                if not os.path.isfile(real):
                    raise JobError("INVALIDO", "no existe el archivo: " + path)
                return real
        raise JobError("RUTA_DENEGADA", "ruta no autorizada para este trabajo: " + path)


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


# ---------------- trabajos ----------------

def _jobs():
    from . import pdf, docx  # import diferido: el saludo no depende de pdfminer
    return {
        "pdf_info": pdf.info,
        "pdf_extract": pdf.extract,
        "pdf_tables": pdf.tables,
        "pdf_symbols": pdf.symbols,
        "pdf_grids": pdf.grids,
        "docx_extract": docx.extract,
    }


JOB_NAMES = ["pdf_info", "pdf_extract", "pdf_tables", "pdf_symbols", "pdf_grids", "docx_extract"]


def capabilities():
    caps = list(JOB_NAMES)
    try:
        import pdfminer  # noqa: F401
    except ImportError:
        caps = [c for c in caps if not c.startswith("pdf_")]
    return caps


def handle(req):
    rid = req.get("id")
    job = req.get("trabajo") or ""
    args = req.get("args") or {}
    if req.get("pe") != PROTOCOL:
        return {"pe": PROTOCOL, "id": rid, "codigo": "VERSION_INCOMPATIBLE", "error": "contrato distinto de %d" % PROTOCOL}
    if job == "hola":
        lo, hi = int(args.get("pe_min", 1)), int(args.get("pe_max", 1))
        if not (lo <= PROTOCOL <= hi):
            return {"pe": PROTOCOL, "id": rid, "codigo": "VERSION_INCOMPATIBLE", "error": "sólo contrato %d" % PROTOCOL}
        return {"pe": PROTOCOL, "id": rid, "codigo": "OK", "resultado": {
            "motor": "python_document_worker", "version": __version__, "pe": PROTOCOL, "capacidades": capabilities(),
            "python": "%d.%d.%d" % sys.version_info[:3]}}
    lim = req.get("limites") or {}
    limit_memory(lim.get("max_mem_mb"))
    t0 = time.perf_counter()
    try:
        fn = _jobs().get(job)
        if fn is None:
            raise JobError("NO_SOPORTADO", "trabajo desconocido: " + job)
        res = fn(args, Paths(req.get("rutas")))
        code = "OK"
        err = None
    except JobError as e:
        res, code, err = {}, e.code, e.message
    except MemoryError:
        res, code, err = {}, "LIMITE_MEMORIA", "el documento excede el límite de memoria del worker"
    except ImportError as e:
        res, code, err = {}, "NO_DISPONIBLE", "falta una dependencia del worker: %s" % e
    except Exception as e:  # un documento malicioso o dañado no tumba el worker
        res, code, err = {}, "ERROR_MOTOR", "%s: %s" % (type(e).__name__, str(e)[:300])
        traceback.print_exc(file=sys.stderr)
    out = {"pe": PROTOCOL, "id": rid, "codigo": code, "resultado": res, "telemetria": {"motor_ms": (time.perf_counter() - t0) * 1000.0}}
    if err:
        out["error"] = err
    try:
        import resource
        out["telemetria"]["memoria_pico_kb"] = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
    except ImportError:
        pass
    return out


def main():
    block_network()
    inp = sys.stdin.buffer
    out = sys.stdout.buffer
    for raw in inp:
        line = raw.decode("utf-8", "replace").strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except ValueError as e:
            resp = {"pe": PROTOCOL, "id": None, "codigo": "INVALIDO", "error": "JSON ilegible: %s" % e}
        else:
            if req.get("trabajo") == "salir":
                break
            resp = handle(req)
        out.write((json.dumps(resp, ensure_ascii=False, separators=(",", ":"), allow_nan=False, default=str) + "\n").encode("utf-8"))
        out.flush()
