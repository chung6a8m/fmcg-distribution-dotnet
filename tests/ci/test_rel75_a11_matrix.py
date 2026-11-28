import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location(
    "rel75_a11_matrix", Path(__file__).resolve().parents[2] / "scripts" / "rel75_a11_matrix.py")
M = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(M)

IDENTITY = ("a" * 40, "b" * 40, "123", "1")
SHA = "C" * 64


def write(root, name, data):
    root.mkdir(parents=True, exist_ok=True)
    (root / name).write_text(json.dumps(data), encoding="utf-8")


def fixture(root, key, nonce="1" * 32):
    arm, tenant, i = key
    source, workflow, run, attempt = IDENTITY
    peer, slot = ("B", "A") if tenant == "tenant-a" else ("A", "B")
    first = {"before-sweep": "SweepBeforeSelect", "c-p-overlap": "CBeforePrerequisite",
             "p-commit-sweep": "SweepBeforeSelect", "outbox-overlap": "OutboxAfterClaimRead",
             "reverse-backlog": "SweepBeforeSelect"}.get(arm)
    boundaries = []
    if first:
        boundaries.append({"Phase": first, "Point": "reached", "Sessions": [
            {"Spid": 51, "BlockingSpid": 0, "HasUserTransaction": True}]})
    if arm in ("c-p-overlap", "outbox-overlap"):
        boundaries.append({"Phase": "PAfterRole", "Point": "reached", "Sessions": [
            {"Spid": 51, "BlockingSpid": 0, "HasUserTransaction": True},
            {"Spid": 52, "BlockingSpid": 0, "HasUserTransaction": True}]})
    if arm == "p-commit-sweep":
        boundaries.extend([{"Phase": "PBeforeCommit", "Point": "reached", "Sessions": []},
                           {"Phase": "SweepInFlight", "Point": "sql-wait", "Sessions": []}])
    s = {"Schema": "REL75_A11_09_V1", "Arm": arm, "Tenant": tenant, "Iteration": i,
         "SourceSha": source, "WorkflowSha": workflow, "Run": run, "Attempt": attempt,
         "Nonce": nonce, "Status": "PASS", "OldPid": 11, "IntakePid": 12,
         "RecoveryPid": 13 if arm == "worker-off" else 12,
         "OracleChecked": 48, "OracleMatched": 48, "PeerUnchanged": True,
         "GraphCapture": "CAPTURED", "Graphs": [], "DiagnosticError": None,
         "ProcessObservation": "LAB-E02/observe.json", "HttpDiagnostic": None,
         "RecoveryDiagnostic": None, "HttpStatus": 202, "Ack": "RECEIVED_COMMITTED",
         "Stage": "oracle-verified", "RecoveryPolls": 2, "IntakeAckUtcMs": 1000,
         "RecoveryUtcMs": 1500, "Boundaries": boundaries,
         "OverlapObserved": arm in ("c-p-overlap", "outbox-overlap"),
         "BacklogCount": {"outbox-overlap": 1, "reverse-backlog": 3}.get(arm, 0),
         "HttpLogSqlNumbers": []}
    e = {"schema": "REL75_A11_EXECUTION_V1", "arm": arm, "tenant": tenant,
         "iteration": i, "source": source, "workflow": workflow, "run": run,
         "attempt": attempt, "trxCount": 1, "testCount": 1, "correctMethod": True,
         "outcome": "Passed", "exitCode": 0}
    o = {"Schema": "APP02A4_SQL_ORACLE_V1", "Group": "A11-09", "TenantId": tenant,
         "SourceHeadSha": source, "WorkflowSha": workflow, "TestRunId": "app02a4-123-1",
         "PeerDatabase": f"Fmcg_App00_{nonce}_{peer}", "Database": f"Fmcg_App00_{nonce}_{slot}",
         "PeerBeforeSha256": SHA, "PeerAfterSha256": SHA, "Status": "PASS",
         "CheckedCells": 48, "MatchedCells": 48,
         "Checkpoints": [{"Phase": "os-pending"}, {"Phase": "os-recovered"}]}
    write(root, "observe.json", {})
    return s, e, o


def save(root, key, s, e, o):
    write(root, "sample.json", s)
    write(root, "execution.json", e)
    write(root, key[1] + "-A11-09-oracle.json", o)


class Rel75A11MatrixTests(unittest.TestCase):
    def test_plan_finite(self):
        self.assertEqual(12, len(M.plan(1)["include"]))
        self.assertEqual(60, len(M.plan(5)["include"]))
        self.assertEqual(60, len(set(M.cells(5))))
        for invalid in (0, 2, 20):
            with self.assertRaises(ValueError):
                M.plan(invalid)

    def test_all_arms_pass_only_with_exact_evidence(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for n, key in enumerate(M.cells(1), 1):
                path = root / ("rel75-a11-123-1-" + "-".join(map(str, key)))
                s, e, o = fixture(path, key, f"{n:032x}")
                save(path, key, s, e, o)
            summary = M.summarize(1, root, IDENTITY)
            self.assertEqual("COMPLETE_NO_REPRODUCTION", summary["status"])
            self.assertEqual({"PASS": 12}, summary["counts"])

    def test_negative_is_not_green_and_1205_is_only_log_signature(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            key = ("c-p-overlap", "tenant-b", 1)
            s, e, o = fixture(root, key)
            s.update(Status="FAIL", HttpStatus=500, Ack=None, Stage="post-restart-P",
                     HttpLogSqlNumbers=[1205], HttpDiagnostic="LAB-E02/http.json",
                     RecoveryDiagnostic="LAB-E02/recovery.json", OracleChecked=24, OracleMatched=24)
            e.update(outcome="Failed", exitCode=2)
            o.update(Status="FAIL", CheckedCells=24, MatchedCells=24)
            write(root, "http.json", {})
            write(root, "recovery.json", {})
            save(root, key, s, e, o)
            self.assertEqual(("1" * 32, "HTTP_500_WITH_1205_LOG", 0),
                             M.validate(root, key, IDENTITY))
            s["Graphs"] = [{"HostPid": 12, "DatabaseId": 7, "Spids": [51, 52],
                            "VictimSpids": [52], "Cycle": True,
                            "Resources": [{"Edges": [{"OwnerSpid": 51, "WaiterSpid": 52},
                                                      {"OwnerSpid": 52, "WaiterSpid": 51}]}]}]
            save(root, key, s, e, o)
            self.assertEqual(1, M.validate(root, key, IDENTITY)[2])
            s["Graphs"][0]["HostPid"] = 99
            save(root, key, s, e, o)
            with self.assertRaisesRegex(ValueError, "GRAPH_SCOPE"):
                M.validate(root, key, IDENTITY)

    def test_missing_recovery_is_distinct_from_500(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            key = ("before-sweep", "tenant-a", 1)
            s, e, o = fixture(root, key)
            s.update(Status="FAIL", Stage="recovery-deadline", RecoveryDiagnostic="LAB-E02/recovery.json")
            e.update(outcome="Failed", exitCode=1)
            o.update(Status="FAIL")
            write(root, "recovery.json", {})
            save(root, key, s, e, o)
            self.assertEqual("RECOVERY_FAILURE", M.validate(root, key, IDENTITY)[1])

    def test_fail_closed_mutations(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            key = ("c-p-overlap", "tenant-b", 1)
            base = fixture(root, key)
            for field, wrong in (("SourceSha", "d" * 40), ("Attempt", "2"), ("Nonce", "bad"),
                                 ("OverlapObserved", False), ("GraphCapture", "UNAVAILABLE"),
                                 ("RecoveryUtcMs", 31001), ("RecoveryPid", 11), ("PeerUnchanged", False),
                                 ("ProcessObservation", "../raw.json"), ("Ack", "UNKNOWN")):
                with self.subTest(field=field):
                    s, e, o = copy.deepcopy(base)
                    s[field] = wrong
                    save(root, key, s, e, o)
                    with self.assertRaises(ValueError):
                        M.validate(root, key, IDENTITY)
            for field, wrong in (("testCount", 2), ("correctMethod", False), ("exitCode", 1)):
                s, e, o = copy.deepcopy(base)
                e[field] = wrong
                save(root, key, s, e, o)
                with self.assertRaises(ValueError):
                    M.validate(root, key, IDENTITY)

    def test_missing_duplicate_and_unexpected_cells_fail_closed(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            keys = M.cells(1)
            for key in keys[:2]:
                path = root / ("rel75-a11-123-1-" + "-".join(map(str, key)))
                save(path, key, *fixture(path, key))
            (root / "unexpected").mkdir()
            summary = M.summarize(1, root, IDENTITY)
            self.assertEqual("INCOMPLETE", summary["status"])
            self.assertEqual(11, summary["counts"]["INCOMPLETE"])
            self.assertEqual(1, summary["unexpected_artifacts"])
            self.assertIn("REUSED_NONCE", [r.get("reason") for r in summary["rows"]])


if __name__ == "__main__":
    unittest.main()
