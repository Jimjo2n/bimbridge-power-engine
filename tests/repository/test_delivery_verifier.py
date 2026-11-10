# SPDX-License-Identifier: GPL-3.0-only
import importlib.util
import io
from pathlib import Path
import unittest
import warnings
import zipfile

path = Path(__file__).resolve().parents[2] / 'tools/repository/verify_delivery.py'
spec = importlib.util.spec_from_file_location('delivery_verifier', path)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class DeliveryArchiveSecurity(unittest.TestCase):
    def archive(self, names):
        data = io.BytesIO()
        with warnings.catch_warnings():
            warnings.simplefilter('ignore', UserWarning)
            with zipfile.ZipFile(data, 'w') as archive:
                for name in names:
                    info = zipfile.ZipInfo('fixture')
                    info.filename = name
                    info.orig_filename = name
                    archive.writestr(info, b'fixture')
        return zipfile.ZipFile(io.BytesIO(data.getvalue()))

    def test_normal_nested_members_are_supported(self):
        with self.archive(['fuente/source.zip', 'SHA256SUMS.txt']) as archive:
            module.validate_members(archive)

    def test_traversal_and_absolute_paths_are_rejected(self):
        for name in ['../escape.txt', '/absolute.txt', 'C:/absolute.txt', r'folder\escape.txt']:
            with self.subTest(name=name), self.archive([name]) as archive:
                with self.assertRaises(ValueError): module.validate_members(archive)

    def test_duplicate_members_are_rejected(self):
        with self.archive(['source.zip', 'source.zip']) as archive:
            with self.assertRaises(ValueError): module.validate_members(archive)
