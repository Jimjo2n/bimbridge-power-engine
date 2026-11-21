#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
r"""
Summarizes the 0.6.0 MCP vs pyRevit benchmark. Input: CSV with one row per run (filled in by whoever runs it; the AI can fill it in):
  motor,carga,corrida,frio,ms_pared,ms_interno,llamadas,chars_enviados,chars_recibidos,tokens_reales,reintentos,error,resultado_qc
  - motor (engine): mcp | pyrevit ; carga (workload): familia | montaje | grupos_vistas_pdf ; frio (cold): 1 only on the first run after opening Revit
  - ms_pared: wall time from when the AI sends until it receives; ms_interno: the "ms" returned by the script
  - chars_*: text the AI wrote / received for that run (including the code if it had to send it)
  - resultado_qc: the JSON/text with the counts (must match between engines for the comparison to count)
Usage: python summarize_bench.py runs.csv [output.md]
Output: warm p50/p95, cold value, failures, and whether the results match. Invents nothing that is not in the CSV.
"""
import csv, sys

def pct(v, p):
    v = sorted(v)
    if not v: return None
    k = (len(v) - 1) * p
    f = int(k); c = min(f + 1, len(v) - 1)
    return round(v[f] + (v[c] - v[f]) * (k - f), 1)

def num(x):
    try: return float(x)
    except Exception: return None

rows = list(csv.DictReader(open(sys.argv[1], encoding="utf-8-sig")))
out = ["# Benchmark 0.6.0 — MCP vs pyRevit (summary of " + sys.argv[1] + ")", "",
       "| Workload | Engine | Cold ms | Warm p50 | p95 | n warm | Failures | Retries | AI chars (p50) | Estimated tokens (p50) | Actual tokens (p50) | Pairs | Estimated − actual error (p50) |",
       "|---|---|---|---|---|---|---|---|---|---|---|---|"]
qc = {}
for workload in sorted(set(r["carga"] for r in rows)):
    for engine in ("mcp", "pyrevit"):
        rs = [r for r in rows if r["carga"] == workload and r["motor"] == engine]
        if not rs: continue
        ok = [r for r in rs if not r.get("error")]
        warm = [num(r["ms_pared"]) for r in ok if r.get("frio") not in ("1", "si", "true") and num(r["ms_pared"]) is not None]
        cold = [num(r["ms_pared"]) for r in ok if r.get("frio") in ("1", "si", "true")]
        sizes = [(num(r.get("chars_enviados")), num(r.get("chars_recibidos"))) for r in ok]
        chars = [a + b if a is not None and b is not None else None for a, b in sizes]
        estimates = [(int(n) + 3) // 4 if n is not None else None for n in chars]
        actual = [num(r.get("tokens_reales")) for r in ok]
        deltas = [e - a for e, a in zip(estimates, actual) if e is not None and a is not None]
        toks = [a for a in actual if a is not None]
        out.append("| %s | %s | %s | %s | %s | %d | %d | %d | %s | %s | %s | %d | %s |" % (
            workload, engine, cold[0] if cold else "—", pct(warm, .5), pct(warm, .95), len(warm),
            len(rs) - len(ok), sum(int(num(r.get("reintentos") or 0) or 0) for r in rs),
            pct([n for n in chars if n is not None], .5), pct([e for e in estimates if e is not None], .5), pct(toks, .5) if toks else "not measured",
            len(deltas), pct(deltas, .5) if deltas else "not measured"))
        qc.setdefault(workload, {})[engine] = set(r.get("resultado_qc", "").strip() for r in ok)
out += ["", "## Same result?", ""]
for workload, m in sorted(qc.items()):
    a, b = m.get("mcp", set()), m.get("pyrevit", set())
    out.append("- %s: %s" % (workload, "match" if a and a == b else "do NOT match or an engine is missing (the timing comparison does not count until resolved)"))
out += ["", "Limits: one machine, one practice model; 1 cold run + ≥10 warm runs is indicative, not strong statistics. "
        "Estimate = ceil(characters/4). Fill in tokens_reales only if the client reports usage for that same exchange; leave it empty if there are only session totals."]
text = "\n".join(out)
if len(sys.argv) > 2: open(sys.argv[2], "w", encoding="utf-8").write(text)
print(text)
