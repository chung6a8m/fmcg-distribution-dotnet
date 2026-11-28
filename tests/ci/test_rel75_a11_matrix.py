import copy
from datetime import datetime, timezone
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
TRIP = "a4967792-c1ee-4e85-b6ab-83c07294a1b7"


def write(root, name, data):
    path = root / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data), encoding="utf-8")


def at(ms):
    return datetime.fromtimestamp(ms / 1000, timezone.utc).isoformat(timespec="milliseconds")


def session(spid, blocker=0, tx=True):
    return {"Spid": spid, "BlockingSpid": blocker, "HasUserTransaction": tx}


def boundary(phase, point, ms, *sessions):
    return {"Phase": phase, "Point": point, "UtcMs": ms, "Sessions": list(sessions)}


def diagnostic(sample, stage, pid, *, worker=True, started=400, captured=1600, cell=True):
    ctx = {"sourceHeadSha": sample["SourceSha"], "workflowSha": sample["WorkflowSha"],
           "testRunId": f"app02a4-{sample['Run']}-{sample['Attempt']}",
           "tenantId": sample["Tenant"], "group": "A11-09", "previousPid": sample["OldPid"]}
    if cell:
        ctx.update(fixtureNonce=sample["Nonce"], arm=sample["Arm"], iteration=sample["Iteration"])
    def stream():
        return {"retainedChars": 100, "discardedChars": 0, "truncated": False, "complete": False,
                "captureError": None, "recoveryObservationsTruncated": False,
                "observations": [], "recoveryObservations": []}
    d = {"context": ctx, "stage": stage, "capturedUtc": at(captured),
         "process": {"pid": pid, "port": 5000, "startedUtc": at(started),
                     "workerEnabled": worker, "exited": False},
         "stdout": stream(), "stderr": stream(), "http": None, "recovery": None}
    if worker:
        first = "tenant-b" if sample["Arm"] == "reverse-backlog" else "tenant-a"
        d["stdout"]["recoveryObservations"] = [
            {"kind": "sweep", "tenant": t, "phase": "processing", "point": "start", "utcMs": started + n}
            for n, t in enumerate((first, "tenant-a" if first == "tenant-b" else "tenant-b"))]
    return d


def fixture(root, key, nonce="1" * 32):
    arm, tenant, i = key
    source, workflow, run, attempt = IDENTITY
    peer, slot = ("B", "A") if tenant == "tenant-a" else ("A", "B")
    f, hashes = M.fixture_contract()
    boundaries = []
    if arm in ("before-sweep", "reverse-backlog"):
        boundaries = [boundary("SweepBeforeSelect", "reached", 500),
                      boundary("SweepBeforeSelect", "released", 1010)]
    elif arm == "c-p-overlap":
        boundaries = [boundary("CBeforePrerequisite", "reached", 500, session(51)),
                      boundary("PAfterRole", "reached", 550, session(51), session(52)),
                      boundary("PAfterRole", "released", 560),
                      boundary("PBeforeCommit", "sql-wait", 600, session(51), session(52, 51)),
                      boundary("CBeforePrerequisite", "released", 610),
                      boundary("PBeforeCommit", "released", 620)]
    elif arm == "p-commit-sweep":
        boundaries = [boundary("SweepBeforeSelect", "reached", 500),
                      boundary("PBeforeCommit", "reached", 550, session(52)),
                      boundary("SweepBeforeSelect", "released", 560),
                      boundary("SweepInFlight", "sql-wait", 600, session(51, 52, False), session(52)),
                      boundary("PBeforeCommit", "released", 610),
                      boundary("CBeforePrerequisite", "released", 620)]
    elif arm == "outbox-overlap":
        boundaries = [boundary("OutboxAfterClaimRead", "reached", 500, session(51)),
                      boundary("PAfterRole", "reached", 550, session(51), session(52)),
                      boundary("OutboxAfterClaimRead", "released", 610),
                      boundary("PAfterRole", "released", 620)]
    previous = {"Pid": 11, "WorkerEnabled": False, "Exited": True, "ExitCode": 1,
                "StartedUtc": at(200), "StoppedUtc": at(300)}
    s = {"Schema": "REL75_A11_09_V2", "Arm": arm, "Tenant": tenant, "Iteration": i,
         "SourceSha": source, "WorkflowSha": workflow, "Run": run, "Attempt": attempt,
         "Nonce": nonce, "Status": "PASS", "OldPid": 11, "IntakePid": 12,
         "PreviousProcess": previous, "RecoveryPid": 13 if arm == "worker-off" else 12,
         "OracleChecked": 48, "OracleMatched": 48, "PeerUnchanged": True,
         "GraphCapture": "CAPTURED", "GraphError": None, "DiagnosticError": None,
         "XeObservation": {"DatabaseId": 7, "Complete": True, "Truncated": False,
                           "TotalEventsProcessed": 0, "EventCount": 0, "TargetDroppedCount": 0,
                           "SessionDroppedEvents": 0, "SessionDroppedBuffers": 0,
                           "LargestDroppedEventSize": 0, "UnprojectedReports": 0, "Graphs": []},
         "ProcessObservation": "LAB-E02/observe.json", "IntakeObservation": None,
         "WorkerOffFacts": None, "HttpDiagnostic": None, "RecoveryDiagnostic": None,
         "HttpStatus": 202, "Ack": "RECEIVED_COMMITTED", "Stage": "oracle-verified",
         "RecoveryPolls": 2, "IntakeAckUtcMs": 1000, "RecoveryUtcMs": 1500, "Boundaries": boundaries,
         "OverlapObserved": arm in ("c-p-overlap", "outbox-overlap"),
         "BacklogCount": {"outbox-overlap": 1, "reverse-backlog": 3}.get(arm, 0),
         "HttpLogSqlNumbers": []}
    e = {"schema": "REL75_A11_EXECUTION_V1", "arm": arm, "tenant": tenant,
         "iteration": i, "source": source, "workflow": workflow, "run": run,
         "attempt": attempt, "trxCount": 1, "testCount": 1, "correctMethod": True,
         "outcome": "Passed", "exitCode": 0}
    db = f"Fmcg_App00_{nonce}_{slot}"
    checkpoints = []
    for phase, state in f["groups"]["A11-09"].items():
        checkpoints.append({"Phase": phase, "TripId": TRIP, "Database": db,
                            "SqlSha256": SHA, "SqlTables": 85, "SqlRows": 100,
                            "SqlStatusHistory": ["RECEIVED", "PENDING_DEPENDENCY"],
                            "Cells": [{"Metric": metric, "Expected": v, "Actual": v, "Difference": 0,
                                       "Match": True} for metric, v in zip(f["columns"], f["states"][state])]})
    o = {"Schema": "APP02A4_SQL_ORACLE_V1", "Group": "A11-09", "TenantId": tenant,
         "SourceHeadSha": source, "WorkflowSha": workflow, "TestRunId": "app02a4-123-1",
         "OracleSha256": sorted(hashes)[0], "PeerDatabase": f"Fmcg_App00_{nonce}_{peer}", "Database": db,
         "PeerBeforeSha256": SHA, "PeerAfterSha256": SHA, "Status": "PASS",
         "FailureDiagnostic": None, "RecoveryDiagnostic": None,
         "CheckedCells": 48, "MatchedCells": 48, "Checkpoints": checkpoints}
    write(root, "LocalDbBootstrap/bootstrap-fixture.json",
          {"schemaVersion": "CI_LOCALDB_BOOTSTRAP_V1", "sourceHeadSha": source,
           "workflowSha": workflow, "runId": run, "runAttempt": attempt, "job": "sample",
           "status": "READY", "mode": "COLD", "instance": "MSSQLLocalDB",
           "failure": None, "readinessAttempts": 1, "deadlineSeconds": 60, "completedUtc": at(100)})
    write(root, s["ProcessObservation"], diagnostic(s, "rel75-observation", s["RecoveryPid"],
                                                    started=1200 if arm == "worker-off" else 400))
    if arm == "worker-off":
        s["IntakeObservation"] = "LAB-E02/intake.json"
        s["WorkerOffFacts"] = f["states"]["ready"]
        write(root, s["IntakeObservation"], diagnostic(s, "rel75-worker-off-intake", 12,
                                                       worker=False, captured=1100))
    return s, e, o


def save(root, key, s, e, o):
    write(root, "sample.json", s)
    write(root, "execution.json", e)
    write(root, key[1] + "-A11-09-oracle.json", o)


def fail(root, s, e, o, http=False):
    s.update(Status="FAIL", Stage="post-restart-P" if http else "recovery-deadline",
             RecoveryDiagnostic="LAB-E02/recovery.json", OracleChecked=24, OracleMatched=24)
    e.update(outcome="Failed", exitCode=1)
    o.update(Status="FAIL", CheckedCells=24, MatchedCells=24, Checkpoints=o["Checkpoints"][:1],
             RecoveryDiagnostic=s["RecoveryDiagnostic"])
    recovery = diagnostic(s, s["Stage"], s["RecoveryPid"], cell=False)
    recovery["recovery"] = {"schema": "REL75_RECOVERY_V1", "fixtureNonce": s["Nonce"], "tripId": TRIP,
                            "previousProcess": {"pid": s["OldPid"], "exited": True},
                            "sql": {"status": "CAPTURED", "state": {"cStatus": "PENDING_DEPENDENCY"},
                                    "error": None}}
    write(root, s["RecoveryDiagnostic"], recovery)
    if http:
        s.update(HttpStatus=500, Ack=None, IntakeAckUtcMs=None, HttpLogSqlNumbers=[1205],
                 HttpDiagnostic="LAB-E02/http.json")
        o["FailureDiagnostic"] = s["HttpDiagnostic"]
        d = diagnostic(s, "post-restart-P", s["IntakePid"])
        d["http"] = {"status": 500, "method": "POST", "path": "/api/synthetic/app02/events/intake"}
        d["stdout"]["observations"] = [{"sqlErrorNumber": 1205}]
        write(root, s["HttpDiagnostic"], d)
    else:
        s["HttpDiagnostic"] = o["FailureDiagnostic"] = s["RecoveryDiagnostic"]


def witness(sample):
    return {"HostPid": 12, "DatabaseId": 7, "Spids": [51, 52], "VictimSpids": [52],
            "EventUtcMs": 700, "Cycle": True,
            "Resources": [{"Kind": "keylock", "Table": "App02EventReceipt", "HobtId": 999,
                           "Edges": [{"OwnerSpid": 51, "WaiterSpid": 52, "OwnerMode": "U", "WaiterMode": "X"},
                                     {"OwnerSpid": 52, "WaiterSpid": 51, "OwnerMode": "X", "WaiterMode": "U"}]}]}


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
                save(path, key, *fixture(path, key, f"{n:032x}"))
            summary = M.summarize(1, root, IDENTITY)
            self.assertEqual("COMPLETE_NO_REPRODUCTION", summary["status"])
            self.assertEqual({"PASS": 12}, summary["counts"])

    def test_negative_is_not_green_and_1205_is_only_log_signature(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            key = ("c-p-overlap", "tenant-b", 1)
            s, e, o = fixture(root, key)
            fail(root, s, e, o, http=True)
            save(root, key, s, e, o)
            self.assertEqual(("1" * 32, "HTTP_500_WITH_1205_LOG", 0), M.validate(root, key, IDENTITY))
            s["XeObservation"].update(Graphs=[witness(s)], EventCount=1, TotalEventsProcessed=1)
            save(root, key, s, e, o)
            self.assertEqual(1, M.validate(root, key, IDENTITY)[2])
            s["XeObservation"]["Graphs"][0]["HostPid"] = 99
            save(root, key, s, e, o)
            with self.assertRaisesRegex(ValueError, "GRAPH_SCOPE"):
                M.validate(root, key, IDENTITY)
            s["XeObservation"]["Graphs"] = [witness(s)]
            s["XeObservation"]["Graphs"][0]["Resources"][0]["Edges"].pop()
            save(root, key, s, e, o)
            with self.assertRaisesRegex(ValueError, "GRAPH_CYCLE_NOT_PROVEN"):
                M.validate(root, key, IDENTITY)
            s["XeObservation"]["Graphs"] = [witness(s)]
            s["HttpLogSqlNumbers"] = [1205, 2601]
            save(root, key, s, e, o)
            with self.assertRaisesRegex(ValueError, "HTTP_SQL_NUMBERS"):
                M.validate(root, key, IDENTITY)

    def test_missing_recovery_is_distinct_from_500(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            key = ("before-sweep", "tenant-a", 1)
            s, e, o = fixture(root, key)
            fail(root, s, e, o)
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

    def test_receipts_require_actual_diagnostic_and_fixture_values(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            key = ("c-p-overlap", "tenant-b", 1)
            s, e, o = fixture(root, key)
            save(root, key, s, e, o)
            write(root, s["ProcessObservation"], {})
            with self.assertRaises((KeyError, ValueError)):
                M.validate(root, key, IDENTITY)
            d = diagnostic(s, "rel75-observation", 12)
            d["context"]["fixtureNonce"] = "f" * 32
            write(root, s["ProcessObservation"], d)
            with self.assertRaisesRegex(ValueError, "DIAGNOSTIC_CELL"):
                M.validate(root, key, IDENTITY)
            write(root, s["ProcessObservation"], diagnostic(s, "rel75-observation", 12))
            o["Checkpoints"][0]["Cells"][0].update(Expected=99, Actual=99)
            save(root, key, s, e, o)
            with self.assertRaisesRegex(ValueError, "ORACLE_CELL_VALUE"):
                M.validate(root, key, IDENTITY)

    def test_lost_xe_and_wrong_directed_wait_cannot_pass(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            key = ("c-p-overlap", "tenant-b", 1)
            base = fixture(root, key)
            for field in ("SessionDroppedEvents", "TargetDroppedCount", "UnprojectedReports"):
                s, e, o = copy.deepcopy(base)
                s["XeObservation"][field] = 1
                save(root, key, s, e, o)
                with self.assertRaisesRegex(ValueError, "XE_DROPPED_OR_UNSCOPED"):
                    M.validate(root, key, IDENTITY)
            s, e, o = copy.deepcopy(base)
            s["Boundaries"][3]["Sessions"] = [session(51, 52), session(52)]
            save(root, key, s, e, o)
            with self.assertRaisesRegex(ValueError, "P_WAIT_DIRECTION"):
                M.validate(root, key, IDENTITY)

    def test_reverse_and_worker_off_controls_have_independent_proof(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            key = ("reverse-backlog", "tenant-b", 1)
            s, e, o = fixture(root, key)
            d = diagnostic(s, "rel75-observation", 12)
            d["stdout"]["recoveryObservations"].reverse()
            for i, v in enumerate(d["stdout"]["recoveryObservations"]):
                v["utcMs"] = 500 + i
            write(root, s["ProcessObservation"], d)
            save(root, key, s, e, o)
            with self.assertRaisesRegex(ValueError, "SWEEP_ORDER_NOT_OBSERVED"):
                M.validate(root, key, IDENTITY)
            key = ("worker-off", "tenant-a", 1)
            s, e, o = fixture(root, key)
            s["WorkerOffFacts"][0] = 0
            save(root, key, s, e, o)
            with self.assertRaisesRegex(ValueError, "WORKER_OFF_CONTROL"):
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
