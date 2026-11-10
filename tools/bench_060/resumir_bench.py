#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
r"""
Resume el benchmark 0.6.0 MCP vs pyRevit. Entrada: CSV con una fila por corrida (lo llena quien ejecuta; la IA puede llenarlo):
  motor,carga,corrida,frio,ms_pared,ms_interno,llamadas,chars_enviados,chars_recibidos,tokens_reales,reintentos,error,resultado_qc
  - motor: mcp | pyrevit ; carga: familia | montaje | grupos_vistas_pdf ; frio: 1 sólo en la primera corrida tras abrir Revit
  - ms_pared: desde que la IA envía hasta que recibe; ms_interno: el "ms" que devuelve el script
  - chars_*: texto que la IA escribió / recibió para esa corrida (incluido el código si lo tuvo que mandar)
  - resultado_qc: el JSON/texto con los conteos (deben coincidir entre motores para que la comparación valga)
Uso: python resumir_bench.py corridas.csv [salida.md]
Salida: p50/p95 en caliente, valor en frío, fallos, y si los resultados coinciden. No inventa nada que no esté en el CSV.
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
out = ["# Benchmark 0.6.0 — MCP vs pyRevit (resumen de " + sys.argv[1] + ")", "",
       "| Carga | Motor | Frío ms | Caliente p50 | p95 | n caliente | Fallos | Reintentos | Chars IA (p50) | Tokens estimados (p50) | Tokens reales (p50) | Pares | Error estimado − real (p50) |",
       "|---|---|---|---|---|---|---|---|---|---|---|---|"]
qc = {}
for carga in sorted(set(r["carga"] for r in rows)):
    for motor in ("mcp", "pyrevit"):
        rs = [r for r in rows if r["carga"] == carga and r["motor"] == motor]
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
            carga, motor, cold[0] if cold else "—", pct(warm, .5), pct(warm, .95), len(warm),
            len(rs) - len(ok), sum(int(num(r.get("reintentos") or 0) or 0) for r in rs),
            pct([n for n in chars if n is not None], .5), pct([e for e in estimates if e is not None], .5), pct(toks, .5) if toks else "no medido",
            len(deltas), pct(deltas, .5) if deltas else "no medido"))
        qc.setdefault(carga, {})[motor] = set(r.get("resultado_qc", "").strip() for r in ok)
out += ["", "## ¿Mismo resultado?", ""]
for carga, m in sorted(qc.items()):
    a, b = m.get("mcp", set()), m.get("pyrevit", set())
    out.append("- %s: %s" % (carga, "coinciden" if a and a == b else "NO coinciden o falta un motor (la comparación de tiempos no vale hasta resolverlo)"))
out += ["", "Límites: una máquina, un modelo de práctica; 1 corrida fría + ≥10 calientes es orientativo, no estadística fuerte. "
        "Estimación = techo(caracteres/4). Complete tokens_reales sólo si el cliente informa uso para ese mismo intercambio; deje vacío si sólo hay totales de sesión."]
text = "\n".join(out)
if len(sys.argv) > 2: open(sys.argv[2], "w", encoding="utf-8").write(text)
print(text)
