# SPDX-License-Identifier: GPL-3.0-only
"""Repository hygiene, first-party licensing, wheel integrity and documentation checks."""
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[2]
def git(*args):
    return subprocess.check_output(["git", "-C", str(ROOT), *args])
paths = [p for p in git("ls-files", "-z").decode().split("\0") if p]
findings = []
patterns = {
    "private-key": rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
    "github-token": rb"gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}",
    "aws-access-key": rb"AKIA[0-9A-Z]{16}",
}
def scan(data, path):
    for label, pattern in patterns.items():
        if re.search(pattern, data):
            findings.append({"path": path, "check": label})

for path in paths:
    p = ROOT / path
    data = p.read_bytes()
    scan(data, path)
    if p.suffix.lower() in {".rvt", ".rfa", ".rte", ".rft", ".pfx", ".pem", ".key", ".exe", ".dll", ".bundle"}:
        findings.append({"path": path, "check": "private-or-generated-file"})
    if p.suffix in {".cs", ".rs", ".py", ".ps1", ".sh"} and b"SPDX-License-Identifier: GPL-3.0-only" not in data[:1000]:
        findings.append({"path": path, "check": "missing-first-party-spdx"})
    if p.suffix == ".whl":
        with zipfile.ZipFile(io.BytesIO(data)) as z:
            for entry in z.infolist():
                if not entry.is_dir():
                    scan(z.read(entry), path + "!" + entry.filename)
lock = ROOT / "workers/python/wheels/LOCK.txt"
for line in lock.read_text(encoding="utf-8-sig").splitlines():
    if not line.strip() or line.lstrip().startswith("#"):
        continue
    name, digest = line.split()
    p = lock.parent / name
    if not p.is_file() or hashlib.sha256(p.read_bytes()).hexdigest() != digest.lower():
        findings.append({"path": str(p.relative_to(ROOT)), "check": "wheel-integrity"})
docs = ["README.md", "README.es.md", "CONTRIBUTING.md", "CODE_OF_CONDUCT.md",
        "docs/INDEX.md", "docs/PROJECT_OVERVIEW.md", "docs/DEVELOPMENT.md",
        "docs/LICENSING.md", "docs/REPOSITORY_SETUP.md", "docs/security/TRUST_MODEL.md",
        "docs/integrations/MCP.md", "docs/operations/MAINTENANCE.md",
        "docs/quality/VALIDATION.md", "docs/performance/BENCHMARKS.md"]
for name in docs:
    p = ROOT / name
    for target in re.findall(r"\]\(([^)]+)\)", p.read_text(encoding="utf-8-sig")):
        if re.match(r"https?://|#", target):
            continue
        target = target.split("#")[0]
        if not (p.parent / target).exists():
            findings.append({"path": name, "check": "broken-link", "target": target})
print(json.dumps({"tracked_files": len(paths), "findings": findings,
                  "limitations": "Heuristic scan; not proof that all private data or secrets are absent."}, indent=2))
sys.exit(1 if findings else 0)
