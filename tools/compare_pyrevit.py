#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
r"""
Quantifiable pyRevit vs BIMBRIDGE comparison WITHOUT re-running tests with both.

Method ("replacement over the real history"):
  1. Takes the real pyRevit runs (folder runs\<date>-<id>\response.json): time, mode, error, output size.
  2. Classifies each run by which MCP tool would replace it and in which phase it exists:
       F1 = already built (revit_context, query, get, check lighting_audit)
       F2+ = planned (geometry, views, sheets, plan/commit, save/sync/export)
       NC = not covered (custom analysis)
  3. For F1 it uses the MCP cost MEASURED in the journal (journal\*.jsonl) and compares.
Usage: python compare_pyrevit.py <runs_folder> <journal_folder_or_file> [--csv output.csv]
"""
import json, os, re, sys, statistics, glob, csv

RULES = [  # (pattern in the title, class, equivalent MCP tool); Spanish words match the historical run titles
    (r"sync|synchroniz|sincroniz|save|export|pdf set|print set", "F4", "document"),
    (r"preview|dry run|apply|place|commit|remove|restore|recover|update|create|hide|separate|rebuild|setup|correct|pass", "F3", "plan/commit"),
    (r"height|altura|clearance|soffit|mounting|support|soporte|floor under|column|geometr|rayo|intersec|opening|box|wall check|teco", "F2", "check clearance_up/clash"),
    (r"sheet|hoja|lámina|viewport|title|graphics", "F2", "check sheets_verify"),
    (r"view range|rcp view|vista|visibilidad|visibility|ocultaci", "F2", "check view_range / capture"),
    (r"tag|etiquet|audit|qc|verif|inventor|count|estado|state|check|type marks|tipos|posici|position|patr|pattern|niveles|worksets|parámetros|parameter|ownership|probe|fuentes|source|families|familias|identity|grid|ejes|recintos|rooms|materiales|direcciones|mapa|map|read|inspect|diagnose|resolve|find", "F1", "query/get/check lighting_audit"),
]

def classify(title):
    t = (title or "").lower()
    for pat, cls, tool in RULES:
        if re.search(pat, t):
            return cls, tool
    return "NC", "-"

def read_runs(folder):
    runs = []
    for d in sorted(os.listdir(folder)):
        p = os.path.join(folder, d, "response.json")
        if not os.path.isfile(p):
            continue
        try:
            r = json.load(open(p, encoding="utf-8"))
        except Exception:
            continue
        script = os.path.join(folder, d, "script.py")
        runs.append({
            "run": d, "modo": r.get("mode"), "estado": r.get("status"),
            "ms": int(r.get("elapsed_ms") or 0), "titulo": r.get("title") or "",
            "error": (str(r.get("error"))[:60] if r.get("status") != "ok" else ""),
            "salida_chars": len(str(r.get("output") or "")) + len(str(r.get("result") or "")),
            "script_chars": os.path.getsize(script) if os.path.isfile(script) else None,
        })
    return runs

def read_journal(path):
    files = [path] if os.path.isfile(path) else glob.glob(os.path.join(path, "*.jsonl"))
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
    runs = read_runs(sys.argv[1])
    mcp = read_journal(sys.argv[2])
    for r in runs:
        r["clase"], r["mcp"] = classify(r["titulo"])
        # The mode governs: any dry_run/modify is a write (phase 3) except document operations.
        if r["modo"] in ("dry_run", "modify") and r["clase"] != "F4":
            r["clase"], r["mcp"] = "F3", "plan/commit"
        # Conservative: a "query" over 5 s probably did geometry or analysis that lighting_audit does not cover.
        if r["clase"] == "F1" and r["ms"] > 5000:
            r["clase"], r["mcp"] = "F2", "check (geometry) or custom analysis"

    print(f"pyRevit runs analyzed: {len(runs)}")
    print(f"MCP measurement (journal): " + ", ".join(f"{k}: n={len(v)} median={statistics.median(v):.0f} ms" for k, v in mcp.items()))
    print()
    print(f"{'class':5} {'runs':>5} {'%':>5} {'failures':>7} {'Revit p50':>10} {'p90':>9} {'total':>9}  MCP tool")
    total_ms = sum(r["ms"] for r in runs) or 1
    for cls in ("F1", "F2", "F3", "F4", "NC"):
        g = [r for r in runs if r["clase"] == cls]
        if not g:
            continue
        ms = [r["ms"] for r in g]
        failures = sum(1 for r in g if r["estado"] != "ok")
        print(f"{cls:5} {len(g):5} {100*len(g)/len(runs):4.0f}% {failures:4} ({100*failures/len(g):2.0f}%) "
              f"{statistics.median(ms)/1000:8.1f} s {pct(ms,.9)/1000:7.1f} s {sum(ms)/60000:6.1f} min  {g[0]['mcp']}")

    f1 = [r for r in runs if r["clase"] == "F1"]
    if f1 and mcp:
        # Conservative assumption: each F1 run equals 2 MCP calls at the measured query/check p90.
        measured = [x for k in ("query", "check") for x in mcp.get(k, [])]
        cost = pct(measured, .9) * 2 if measured else 0
        py = sum(r["ms"] for r in f1)
        failures = sum(1 for r in f1 if r["estado"] != "ok")
        out = [r["salida_chars"] for r in f1 if r["estado"] == "ok"]
        print()
        print("Replacement of what is already built (F1):")
        print(f"  pyRevit Revit time: {py/60000:.1f} min over {len(f1)} runs; estimated MCP: {cost*len(f1)/60000:.2f} min "
              f"(2 calls × measured p90 {pct(measured,.9)} ms) → reduction {100*(1-cost*len(f1)/py):.0f} %")
        print(f"  Script failures: {failures}/{len(f1)} ({100*failures/len(f1):.0f} %). With typed operations there are no "
              f"AttributeError/TypeError errors; a wrong argument comes back as 'invalido' before touching Revit.")
        print(f"  Output returned to the AI: median {statistics.median(out):.0f} characters per run (pyRevit).")
        with_script = [r["script_chars"] for r in f1 if r["script_chars"]]
        if with_script:
            print(f"  Script sent by the AI: median {statistics.median(with_script):.0f} characters; a typical MCP query ~80.")
        else:
            print("  Script size: not available in this copy (script.py missing); run the script on the PC to measure it.")

    if len(sys.argv) > 4 and sys.argv[3] == "--csv":
        with open(sys.argv[4], "w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=list(runs[0].keys()))
            w.writeheader(); w.writerows(runs)

if __name__ == "__main__":
    main()
