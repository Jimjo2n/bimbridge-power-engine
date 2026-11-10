# SPDX-License-Identifier: GPL-3.0-only
"""Inspect reachable Git blobs without printing sensitive matching values."""
import io
import json
import re
import subprocess
import sys
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
def git(*args, data=None):
    return subprocess.check_output(["git", "-C", str(ROOT), *args], input=data)
ref = sys.argv[1] if len(sys.argv) > 1 else "HEAD"
head = git("rev-parse", "--verify", ref + "^{commit}").decode().strip()
entries = [line.partition(" ") for line in git("rev-list", "--objects", head).decode().splitlines()]
names = {sha: path for sha, _, path in entries if path}
object_ids = [sha for sha, _, path in entries if path]
checks = git("cat-file", "--batch-check=%(objectname) %(objecttype)", data=("".join(x+"\n" for x in object_ids)).encode()).decode().splitlines()
blobs = [line.split()[0] for line in checks if line.endswith(" blob")]
raw = io.BytesIO(git("cat-file", "--batch", data=("".join(x+"\n" for x in blobs)).encode()))
patterns = {
    "private-key": rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
    "github-token": rb"gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}",
    "aws-access-key": rb"AKIA[0-9A-Z]{16}",
}
findings = []
for _ in blobs:
    sha, kind, size = raw.readline().decode().split()
    data = raw.read(int(size)); raw.read(1)
    for label, pattern in patterns.items():
        if re.search(pattern, data):
            findings.append({"category": label, "path": names.get(sha), "blob": sha})
print(json.dumps({"head": head, "blobs_checked": len(blobs), "findings": findings,
    "limitations": "Heuristic triage only; current-tree checks also inspect wheel contents. Does not certify absence of all private information."}, indent=2))
sys.exit(1 if findings else 0)
