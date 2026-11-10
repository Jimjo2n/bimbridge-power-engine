# SPDX-License-Identifier: GPL-3.0-only
"""Fail on audit errors or declared known vulnerabilities; never relabel an unavailable scan as PASS."""
import json
from pathlib import Path
import sys
report = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
findings = []
def walk(value):
    if isinstance(value, dict):
        if value.get("vulnerabilities"):
            findings.append({"package": value.get("id"), "vulnerabilities": value["vulnerabilities"]})
        if value.get("level", "").lower() == "error":
            findings.append({"audit_error": value.get("text", "NuGet audit error")})
        for child in value.values(): walk(child)
    elif isinstance(value, list):
        for child in value: walk(child)
walk(report)
print(json.dumps({"findings": findings}, indent=2))
sys.exit(1 if findings else 0)
