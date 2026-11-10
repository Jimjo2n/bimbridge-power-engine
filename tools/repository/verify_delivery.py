# SPDX-License-Identifier: GPL-3.0-only
"""Verify an immutable delivery without executing its scripts, installer or Revit."""
import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
import tempfile
import zipfile

def validate_members(archive):
    seen = set()
    for item in archive.infolist():
        name = item.orig_filename
        path = PurePosixPath(name)
        if path.as_posix() in seen or path.is_absolute() or ".." in path.parts or "\\" in name or re.match(r"^[A-Za-z]:", name):
            raise ValueError("Unsafe or duplicate archive entry")
        seen.add(path.as_posix())

def digest_file(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()

def verify(args):
    if digest_file(args.package) != args.sha256.lower():
        raise ValueError("Outer package SHA-256 mismatch")
    if not re.fullmatch(r"[0-9a-fA-F]{40}", args.commit):
        raise ValueError("Full 40-character source commit required")
    with zipfile.ZipFile(args.package) as outer:
        validate_members(outer)
        checked = 0
        for line in outer.read(args.checksums).decode("utf-8-sig").splitlines():
            if not line.strip(): continue
            expected, name = line.split(None, 1)
            name = name.strip().lstrip("*")
            digest = hashlib.sha256()
            with outer.open(name) as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(block)
            if digest.hexdigest() != expected.lower():
                raise ValueError("Delivery checksum mismatch")
            checked += 1
        with tempfile.TemporaryDirectory(prefix="bimbridge-delivery-") as temp:
            temp = Path(temp); bundle = temp / "incoming.bundle"; repo = temp / "history.git"
            bundle.write_bytes(outer.read(args.bundle))
            def git(*parts):
                return subprocess.check_output(["git", "-C", str(repo), *parts], stderr=subprocess.PIPE)
            subprocess.run(["git", "init", "--bare", str(repo)], check=True, capture_output=True)
            git("bundle", "verify", str(bundle))
            git("fetch", str(bundle), args.bundle_ref + ":refs/heads/incoming")
            actual = git("rev-parse", "incoming").decode().strip()
            if actual.lower() != args.commit.lower(): raise ValueError("Bundle source commit mismatch")
            git("fsck", "--full")
            git_zip = git("archive", "--format=zip", actual)
            with zipfile.ZipFile(io.BytesIO(git_zip)) as expected, zipfile.ZipFile(io.BytesIO(outer.read(args.source))) as supplied:
                validate_members(supplied)
                one = {x.filename: hashlib.sha256(expected.read(x)).hexdigest() for x in expected.infolist() if not x.is_dir()}
                two = {x.filename: hashlib.sha256(supplied.read(x)).hexdigest() for x in supplied.infolist() if not x.is_dir()}
                if one != two: raise ValueError("Source archive differs from Git commit tree")
            return {"package_sha256": args.sha256.lower(), "source_commit": actual,
                    "delivery_hashes_verified": checked, "source_files": len(one),
                    "available_commits": int(git("rev-list", "--count", "incoming")),
                    "status": "PASS", "privacy_review": "REQUIRED_BEFORE_UPLOAD",
                    "limitations": "Verifies available history and integrity, not upstream completeness, safety or native acceptance."}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("--sha256", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--bundle", required=True, help="Bundle member path inside delivery")
    parser.add_argument("--source", required=True, help="Source ZIP member path inside delivery")
    parser.add_argument("--checksums", default="SHA256SUMS.txt")
    parser.add_argument("--bundle-ref", default="HEAD")
    args = parser.parse_args()
    print(json.dumps(verify(args), indent=2))

if __name__ == "__main__":
    main()
