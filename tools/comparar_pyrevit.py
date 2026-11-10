#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
r"""
Comparación cuantificable pyRevit vs PDI Revit MCP SIN volver a correr pruebas con ambos.

Método ("reemplazo sobre el historial real"):
  1. Toma los runs reales de pyRevit (carpeta runs\<fecha>-<id>\response.json): tiempo, modo, error, tamaño de salida.
  2. Clasifica cada run según qué herramienta del MCP lo reemplazaría y en qué fase existe:
       F1 = ya construido (revit_context, query, get, check lighting_audit)
       F2+ = previsto (geometría, vistas, láminas, plan/commit, guardar/sync/exportar)
       NC = no cubierto (análisis a medida)
  3. Para lo de F1 usa el costo MEDIDO del MCP en el diario (journal\*.jsonl) y compara.
Uso: python comparar_pyrevit.py <carpeta_runs> <carpeta_journal_o_archivo> [--csv salida.csv]
"""
import json, os, re, sys, statistics, glob, csv

REGLAS = [  # (patrón en el título, clase, herramienta MCP equivalente)
    (r"sync|synchroniz|sincroniz|save|export|pdf set|print set", "F4", "document"),
    (r"preview|dry run|apply|place|commit|remove|restore|recover|update|create|hide|separate|rebuild|setup|correct|pass", "F3", "plan/commit"),
    (r"height|altura|clearance|soffit|mounting|support|soporte|floor under|column|geometr|rayo|intersec|opening|box|wall check|teco", "F2", "check clearance_up/clash"),
    (r"sheet|hoja|lámina|viewport|title|graphics", "F2", "check sheets_verify"),
    (r"view range|rcp view|vista|visibilidad|visibility|ocultaci", "F2", "check view_range / capture"),
    (r"tag|etiquet|audit|qc|verif|inventor|count|estado|state|check|type marks|tipos|posici|position|patr|pattern|niveles|worksets|parámetros|parameter|ownership|probe|fuentes|source|families|familias|identity|grid|ejes|recintos|rooms|materiales|direcciones|mapa|map|read|inspect|diagnose|resolve|find", "F1", "query/get/check lighting_audit"),
]

def clasificar(title):
    t = (title or "").lower()
    for pat, cls, tool in REGLAS:
        if re.search(pat, t):
            return cls, tool
    return "NC", "-"

def leer_runs(carpeta):
    runs = []
    for d in sorted(os.listdir(carpeta)):
        p = os.path.join(carpeta, d, "response.json")
        if not os.path.isfile(p):
            continue
        try:
            r = json.load(open(p, encoding="utf-8"))
        except Exception:
            continue
        script = os.path.join(carpeta, d, "script.py")
        runs.append({
            "run": d, "modo": r.get("mode"), "estado": r.get("status"),
            "ms": int(r.get("elapsed_ms") or 0), "titulo": r.get("title") or "",
            "error": (str(r.get("error"))[:60] if r.get("status") != "ok" else ""),
            "salida_chars": len(str(r.get("output") or "")) + len(str(r.get("result") or "")),
            "script_chars": os.path.getsize(script) if os.path.isfile(script) else None,
        })
    return runs

def leer_journal(ruta):
    files = [ruta] if os.path.isfile(ruta) else glob.glob(os.path.join(ruta, "*.jsonl"))
    ms = {}
    for f in files:
        for line in open(f, encoding="utf-8"):
            try:
                e = json.loads(line)
            except Exception:
                continue
            if e.get("op") in ("context", "query", "check", "sets") and e.get("estado") == "ok":
                ms.setdefault(e["op"], []).append(e.get("ms", 0))
    return ms

def pct(v, p):
    v = sorted(v)
    return v[min(len(v) - 1, int(round(p * (len(v) - 1))))] if v else 0

def main():
    if len(sys.argv) < 3:
        print(__doc__); sys.exit(1)
    runs = leer_runs(sys.argv[1])
    mcp = leer_journal(sys.argv[2])
    for r in runs:
        r["clase"], r["mcp"] = clasificar(r["titulo"])
        # El modo manda: cualquier dry_run/modify es una escritura (fase 3) salvo operaciones de documento.
        if r["modo"] in ("dry_run", "modify") and r["clase"] != "F4":
            r["clase"], r["mcp"] = "F3", "plan/commit"
        # Conservador: una "consulta" de más de 5 s probablemente hizo geometría o análisis que lighting_audit no cubre.
        if r["clase"] == "F1" and r["ms"] > 5000:
            r["clase"], r["mcp"] = "F2", "check (geometría) o análisis a medida"

    print(f"Runs de pyRevit analizados: {len(runs)}")
    print(f"Medición MCP (diario): " + ", ".join(f"{k}: n={len(v)} mediana={statistics.median(v):.0f} ms" for k, v in mcp.items()))
    print()
    print(f"{'clase':5} {'runs':>5} {'%':>5} {'fallos':>7} {'Revit p50':>10} {'p90':>9} {'total':>9}  herramienta MCP")
    total_ms = sum(r["ms"] for r in runs) or 1
    for cls in ("F1", "F2", "F3", "F4", "NC"):
        g = [r for r in runs if r["clase"] == cls]
        if not g:
            continue
        ms = [r["ms"] for r in g]
        fallos = sum(1 for r in g if r["estado"] != "ok")
        print(f"{cls:5} {len(g):5} {100*len(g)/len(runs):4.0f}% {fallos:4} ({100*fallos/len(g):2.0f}%) "
              f"{statistics.median(ms)/1000:8.1f} s {pct(ms,.9)/1000:7.1f} s {sum(ms)/60000:6.1f} min  {g[0]['mcp']}")

    f1 = [r for r in runs if r["clase"] == "F1"]
    if f1 and mcp:
        # Supuesto conservador: cada run de F1 equivale a 2 llamadas MCP con el p90 medido de query/check.
        medidos = [x for k in ("query", "check") for x in mcp.get(k, [])]
        costo = pct(medidos, .9) * 2 if medidos else 0
        py = sum(r["ms"] for r in f1)
        fallos = sum(1 for r in f1 if r["estado"] != "ok")
        out = [r["salida_chars"] for r in f1 if r["estado"] == "ok"]
        print()
        print("Reemplazo de lo ya construido (F1):")
        print(f"  Tiempo de Revit pyRevit: {py/60000:.1f} min en {len(f1)} runs; MCP estimado: {costo*len(f1)/60000:.2f} min "
              f"(2 llamadas × p90 medido {pct(medidos,.9)} ms) → reducción {100*(1-costo*len(f1)/py):.0f} %")
        print(f"  Fallos por script: {fallos}/{len(f1)} ({100*fallos/len(f1):.0f} %). Con operaciones tipadas no existen errores de "
              f"AttributeError/TypeError; un argumento mal puesto vuelve como 'invalido' antes de tocar Revit.")
        print(f"  Salida devuelta a la IA: mediana {statistics.median(out):.0f} caracteres por run (pyRevit).")
        con_script = [r["script_chars"] for r in f1 if r["script_chars"]]
        if con_script:
            print(f"  Script enviado por la IA: mediana {statistics.median(con_script):.0f} caracteres; una consulta MCP típica ~80.")
        else:
            print("  Tamaño de scripts: no disponible en esta copia (falta script.py); corra el script en la PC para medirlo.")

    if len(sys.argv) > 4 and sys.argv[3] == "--csv":
        with open(sys.argv[4], "w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=list(runs[0].keys()))
            w.writeheader(); w.writerows(runs)

if __name__ == "__main__":
    main()
