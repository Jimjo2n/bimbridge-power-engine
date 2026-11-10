# SPDX-License-Identifier: GPL-3.0-only
"""Create a declared dependency inventory; this is not a full resolved SBOM or license verdict."""
import email
import hashlib
import json
from pathlib import Path
import tomllib
import xml.etree.ElementTree as ET
import zipfile
root = Path(__file__).resolve().parents[2]
result = {"nuget_declared": [], "cargo_resolved": [], "python_wheels": [],
          "limitations": "NuGet is declared rather than a complete resolved graph. Review all transitive notices before distribution."}
for path in sorted(root.rglob("*.csproj")):
    if any(x in path.parts for x in ("bin", "obj", "target")): continue
    for item in ET.parse(path).iter("PackageReference"):
        result["nuget_declared"].append({"project": str(path.relative_to(root)), "package": item.get("Include"), "version": item.get("Version")})
cargo = tomllib.loads((root / "native/power_engine/Cargo.lock").read_text())
result["cargo_resolved"] = [{"name": x["name"], "version": x["version"], "source": x.get("source"), "checksum": x.get("checksum")} for x in cargo.get("package", [])]
for path in sorted((root / "workers/python/wheels").glob("*.whl")):
    with zipfile.ZipFile(path) as z:
        meta = next(n for n in z.namelist() if n.endswith(".dist-info/METADATA"))
        message = email.message_from_bytes(z.read(meta))
        notices = [n for n in z.namelist() if "license" in n.lower() or "copying" in n.lower()]
        result["python_wheels"].append({"file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "name": message.get("Name"), "version": message.get("Version"), "declared_license": message.get("License-Expression") or message.get("License"), "notice_paths": notices})
print(json.dumps(result, indent=2))
