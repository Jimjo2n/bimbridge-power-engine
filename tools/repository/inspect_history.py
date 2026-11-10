# SPDX-License-Identifier: GPL-3.0-only
# SPDX-License-Identifier: GPL-3.0-only
"""Inspect reachable Git blobs without printing secret values."""
import json, re, subprocess, sys
from pathlib import Path
root = Path(__file__).resolve().parents[2]
def git(*args):
    return subprocess.check_output(["git", "-C", str(root), *args])
ref = sys.argv[1] if len(sys.argv) > 1 else "HEAD"
head = git("rev-parse", "--verify", ref + "^{commit}").decode().strip()
objects = git("rev-list", "--objects", head).decode().splitlines()
patterns = {
    "private-key": rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
    "github-token": rb"gh[pousr]_[A-Za-z0-9]{30,}",
    "aws-access-key": rb"AKIA[0-9A-Z]{16}",
}
findings = []
blobs = 0
for entry in objects:
    sha, _, path = entry.partition(" ")
    if not path or git("cat-file", "-t", sha).strip() != b"blob":
        continue
    blobs += 1
    data = git("cat-file", "blob", sha)
    for name, pattern in patterns.items():
        if re.search(pattern, data):
            findings.append({"category":name,"path":path,"blob":sha})
    if Path(path).suffix.lower() in {".rvt",".rfa",".pdf",".docx",".pfx",".pem",".key"} or Path(path).name.lower() in {"revitapi.dll","revitapiui.dll"}:
        findings.append({"category":"restricted-or-private-file-review","path":path,"blob":sha})
result={"head":head,"blobs_checked":blobs,"findings":findings,"limitations":"Heuristic triage only. Does not inspect archives or prove absence of secrets. Review reachable history and dependency notices before upload."}
print(json.dumps(result, indent=2))
sys.exit(1 if findings else 0)
