# SPDX-License-Identifier: GPL-3.0-only
"""Prepare the source worker's test dependencies from the existing hash-verified packaging routine."""
import importlib.util
from pathlib import Path
import shutil
import tempfile

ROOT = Path(__file__).resolve().parents[2]
target = ROOT / 'workers/python/vendor'
if not target.resolve().is_relative_to(ROOT.resolve()) or target.exists():
    raise SystemExit('Use a fresh checkout: worker vendor target must be absent and inside the repository')
source = ROOT / 'tools/power_engine/vendor_python.py'
spec = importlib.util.spec_from_file_location('bimbridge_vendor', source)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
with tempfile.TemporaryDirectory(prefix='bimbridge-ci-python-') as temp:
    temporary_root = Path(temp).resolve()
    worker = temporary_root / 'worker'
    if not worker.resolve().is_relative_to(temporary_root):
        raise SystemExit('Temporary worker outside expected directory')
    module.main(str(worker))
    shutil.copytree(worker / 'vendor', target)
print('Prepared hash-verified pure Python dependencies; AES support remains limited as documented.')
