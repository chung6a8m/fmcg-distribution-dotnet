import json
from pathlib import Path
import tempfile
import unittest

from tools.app02b_lab.catalog import FIXTURE, RUNTIME_PROBES, load_catalog


class CatalogTests(unittest.TestCase):
    def test_original_cases_and_separate_runtime_coverage(self):
        cases = load_catalog(FIXTURE)
        self.assertEqual(len(cases), 30)
        self.assertEqual(set(RUNTIME_PROBES), {"RUNTIME-01", "RUNTIME-02"})
        self.assertFalse(set(cases) & set(RUNTIME_PROBES))
        self.assertEqual(cases["B01-01"].expected, "DENY_CROSS_TENANT_DATA_AND_METADATA")

    def test_rejects_each_fixture_mutation(self):
        source = FIXTURE.read_bytes()
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "alternative.json"
            for variant in (
                source + b"\n",
                source.replace(b'"B01-01":', b'"B01-02":'),
                source.replace(b'"B01-01":', b'"B06-01":'),
                source.replace(b'"caseCount": 30,', b'"caseCount": 30, "caseCount": 30,'),
                source.replace(b'DENY_CROSS_TENANT_DATA_AND_METADATA', b'UNEXPECTED'),
                source.replace(b'PEER_READ_WRITE_ZERO', b'INVARIANT_CHANGED'),
                source.replace(b'"HTTP"', b'"FORGED_KIND"', 1),
                b'{"cases":{}}',
            ):
                with self.subTest(variant=variant[:60]):
                    path.write_bytes(variant)
                    with self.assertRaises(ValueError):
                        load_catalog(path)
            body = json.loads(source)
            body["cases"]["B06-01"] = body["cases"]["B01-01"]
            path.write_text(json.dumps(body))
            with self.assertRaises(ValueError):
                load_catalog(path)
