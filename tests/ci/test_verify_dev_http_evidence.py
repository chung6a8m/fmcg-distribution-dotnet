import json
from pathlib import Path
import tempfile
import unittest
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from verify_dev_http_evidence import digest, verify


class DevHttpEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.run_id = "c" * 32
        self.expected = {"sourceHead": "a" * 40, "checkout": "b" * 40, "run": "10", "attempt": "1",
                         "scenario": "F06", "arm": "baseline", "iteration": 1}
        self.report = b'<testsuites tests="1" failures="0" errors="0" skipped="0"><testsuite><testcase classname="cost-adjustments.spec.ts"/></testsuite></testsuites>'
        self.folder = Path("TestResults/dev-f06") / self.run_id
        self.report_path = Path("web/test-results-app02c5") / self.run_id / "junit.xml"
        self.write_comparison()
        self.write(self.report_path, self.report)
        self.sql = {"runId": self.run_id, "source": self.expected["checkout"], "matches": True,
                    "browserReportSha256": digest(self.report)}
        self.write(self.folder / "sql-result.json", self.sql)

    def write(self, path, data):
        p = self.root / path
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(data if isinstance(data, bytes) else json.dumps(data).encode())

    def write_comparison(self):
        self.write(Path("TestResults/dev-http-comparison.json"),
                   {"schema": "DEV_HTTP_COMPARISON_V1", **self.expected, "scenarioOutcome": "success",
                    "witnessEnabled": self.expected["arm"] == "instrumented"})

    def test_run_bound_f06_report(self):
        result = verify(self.root, self.expected)
        self.assertEqual(result["browserReportSha256"], digest(self.report))
        self.assertEqual(result["readyObservers"], 0)

    def test_missing_junit(self):
        (self.root / self.report_path).unlink()
        with self.assertRaises(OSError):
            verify(self.root, self.expected)

    def test_digest_mismatch(self):
        self.sql["browserReportSha256"] = "0" * 64
        self.write(self.folder / "sql-result.json", self.sql)
        with self.assertRaisesRegex(ValueError, "junitDigest"):
            verify(self.root, self.expected)

    def test_wrong_test_or_skipped_report(self):
        for data in (self.report.replace(b"cost-adjustments", b"visits"),
                     self.report.replace(b'skipped="0"', b'skipped="1"')):
            with self.subTest(data=data):
                self.write(self.report_path, data)
                with self.assertRaises(ValueError):
                    verify(self.root, self.expected)

    def test_wrong_fixture_or_checkout(self):
        self.sql["source"] = "f" * 40
        self.write(self.folder / "sql-result.json", self.sql)
        with self.assertRaisesRegex(ValueError, "sqlIdentity"):
            verify(self.root, self.expected)

    def test_instrumented_observers_are_required(self):
        self.expected["arm"] = "instrumented"
        self.write_comparison()
        with self.assertRaisesRegex(ValueError, "observerInventory"):
            verify(self.root, self.expected)

    def test_ready_and_timeout_observers(self):
        self.expected["arm"] = "instrumented"
        self.write_comparison()
        common = {"source": self.expected["checkout"], "runId": self.run_id, "profile": "dev-f06"}
        self.write(self.folder / "d2-peer-initial-startup.json",
                   {"schema": "D2_PEER_STARTUP_V1", **common, "direction": "initial",
                    "tenantAStarted": True, "tenantBStarted": True})
        for tenant in ("tenant-a", "tenant-b"):
            prefix = self.folder / ("d2-peer-initial-" + tenant)
            identity = {**common, "scope": "initial", "tenant": tenant}
            self.write(str(prefix) + "-observer.json",
                       {"schema": "DEV_SQL_OBSERVER_V1", **identity, "pid": 123, "status": "ready",
                        "readinessSource": "flushedSample", "startupProof": {"status": "ready", "firstSampleUtc": "sample"},
                        "startupBudgetMs": 4000, "startupElapsedMs": 1500, "startupFailure": "none"})
            self.write(str(prefix) + "-cleanup.json",
                       {"schema": "DEV_SQL_OBSERVER_CLEANUP_V1", **identity, "cleanup": "exited", "exitCode": 0})
            rows = [{"event": "start", **identity, "pid": 123}, {"event": "available"},
                    {"event": "sample", "truncated": False, "utc": "sample"},
                    {"event": "sample", "finalSample": True},
                    {"event": "stop", "reason": "parentEnded", "finalSampleStatus": "usable"}]
            self.write(str(prefix) + "-sql.ndjson", "\n".join(json.dumps(row) for row in rows).encode())
        self.assertEqual(verify(self.root, self.expected)["readyObservers"], 2)
        p = self.folder / "d2-peer-initial-tenant-a-observer.json"
        row = json.loads((self.root / p).read_text())
        row["status"] = "startupTimeout"
        self.write(p, row)
        with self.assertRaisesRegex(ValueError, "observerAdmission"):
            verify(self.root, self.expected)


if __name__ == "__main__":
    unittest.main()
