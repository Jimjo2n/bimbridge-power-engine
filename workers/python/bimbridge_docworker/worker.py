# SPDX-License-Identifier: GPL-3.0-only
"""Python Worker loop: contract, guards (paths, network, memory) and job dispatch."""
import hashlib
import json
import os
import socket
import sys
import time
import traceback

from . import __version__, PROTOCOL

_HERE = os.path.dirname(os.path.abspath(__file__))
# bundled pure dependencies (pdfminer.six, charset_normalizer) at the END: system ones win if present
_VENDOR = os.path.join(os.path.dirname(_HERE), "vendor")
if os.path.isdir(_VENDOR) and _VENDOR not in sys.path:
    sys.path.append(_VENDOR)


class JobError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code
        self.message = message


# ---------------- guards ----------------

def _no_network(*_a, **_k):
    raise JobError("RUTA_DENEGADA", "the Python Worker has no network access")


def block_network():
    """No network by default: no document leaves the machine by accident (not even through a library)."""
    socket.socket = _no_network  # type: ignore
    socket.create_connection = _no_network  # type: ignore
    socket.getaddrinfo = _no_network  # type: ignore


_mem_limit_set = [0]


def limit_memory(mb):
    """Process memory limit (POSIX). On Windows the C# core enforces it (kills the process if it is exceeded)."""
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
    """Authorized paths for ONE job. The real path (links resolved) is compared against real prefixes."""

    def __init__(self, allowed):
        self.allowed = [os.path.realpath(p) for p in (allowed or []) if p]

    def check(self, path):
        if not path or not isinstance(path, str):
            raise JobError("INVALIDO", "missing document path")
        real = os.path.realpath(path)
        for a in self.allowed:
            if os.path.normcase(real) == os.path.normcase(a) or os.path.normcase(real).startswith(os.path.normcase(a.rstrip(os.sep) + os.sep)):
                if not os.path.isfile(real):
                    raise JobError("INVALIDO", "file does not exist: " + path)
                return real
        raise JobError("RUTA_DENEGADA", "path not authorized for this job: " + path)


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


# ---------------- jobs ----------------

def _jobs():
    from . import pdf, docx, xlsx  # deferred import: the handshake does not depend on pdfminer
    return {
        "pdf_info": pdf.info,
        "pdf_verify": pdf.verify,
        "pdf_extract": pdf.extract,
        "pdf_tables": pdf.tables,
        "pdf_symbols": pdf.symbols,
        "pdf_grids": pdf.grids,
        "docx_extract": docx.extract,
        "xlsx_feeders": xlsx.read_feeders,
    }


JOB_NAMES = ["pdf_info", "pdf_verify", "pdf_extract", "pdf_tables", "pdf_symbols", "pdf_grids", "docx_extract", "xlsx_feeders"]


def capabilities():
    caps = list(JOB_NAMES)
    try:
        import pdfminer  # noqa: F401
    except ImportError:
        caps = [c for c in caps if not c.startswith("pdf_")]
    return caps


def handle(req):
    rid = req.get("id")
    job = req.get("job") or ""
    args = req.get("args") or {}
    if req.get("pe") != PROTOCOL:
        return {"pe": PROTOCOL, "id": rid, "code": "VERSION_INCOMPATIBLE", "error": "contract other than %d" % PROTOCOL}
    if job == "hola":
        lo, hi = int(args.get("pe_min", 1)), int(args.get("pe_max", 1))
        if not (lo <= PROTOCOL <= hi):
            return {"pe": PROTOCOL, "id": rid, "code": "VERSION_INCOMPATIBLE", "error": "only contract %d" % PROTOCOL}
        return {"pe": PROTOCOL, "id": rid, "code": "OK", "result": {
            "motor": "python_document_worker", "version": __version__, "pe": PROTOCOL, "capacidades": capabilities(),
            "python": "%d.%d.%d" % sys.version_info[:3]}}
    lim = req.get("limits") or {}
    limit_memory(lim.get("max_mem_mb"))
    t0 = time.perf_counter()
    try:
        fn = _jobs().get(job)
        if fn is None:
            raise JobError("NO_SOPORTADO", "unknown job: " + job)
        res = fn(args, Paths(req.get("paths")))
        code = "OK"
        err = None
    except JobError as e:
        res, code, err = {}, e.code, e.message
    except MemoryError:
        res, code, err = {}, "LIMITE_MEMORIA", "the document exceeds the worker memory limit"
    except ImportError as e:
        res, code, err = {}, "NO_DISPONIBLE", "missing worker dependency: %s" % e
    except Exception as e:  # a malicious or damaged document does not bring the worker down
        res, code, err = {}, "ERROR_MOTOR", "%s: %s" % (type(e).__name__, str(e)[:300])
        traceback.print_exc(file=sys.stderr)
    out = {"pe": PROTOCOL, "id": rid, "code": code, "result": res, "telemetry": {"motor_ms": (time.perf_counter() - t0) * 1000.0}}
    if err:
        out["error"] = err
    try:
        import resource
        out["telemetry"]["memoria_pico_kb"] = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
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
            resp = {"pe": PROTOCOL, "id": None, "code": "INVALIDO", "error": "unreadable JSON: %s" % e}
        else:
            if req.get("job") == "salir":
                break
            resp = handle(req)
        out.write((json.dumps(resp, ensure_ascii=False, separators=(",", ":"), allow_nan=False, default=str) + "\n").encode("utf-8"))
        out.flush()
