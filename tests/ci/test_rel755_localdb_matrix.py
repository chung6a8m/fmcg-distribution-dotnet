"""Native evidence validation: synthetic receipts only, no live SQL."""
import copy
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from test_rel75_localdb_matrix import receipt, SOURCE, WORKFLOW, RUN, ATTEMPT
import rel755_localdb_matrix as m

IDENTITY = (SOURCE, WORKFLOW, RUN, ATTEMPT)


def utc(ms):
    return f"2026-11-28T00:00:{ms//1000:02d}.{ms%1000:03d}Z"


def vol(resolution="EXISTING_PATH", physical=4096):
    return {"status":"OK","rootDrive":"C:","volumeIdentitySha256":"f"*64,"resolution":resolution,
            "sector":{"status":"OK","logicalBytes":512,"physicalAtomicityBytes":physical,
                      "physicalPerformanceBytes":physical,"effectiveAtomicityBytes":4096,
                      "maxPhysicalBytes":physical}}


def fixture(arm, failed=False):
    orig=receipt(arm,state="BOOTSTRAP_FAILED" if failed else "READY")
    stages=orig["bootstrap"]
    orig.update(startedUtc=utc(100),completedUtc=utc(5500))
    for i,s in enumerate(stages,1):
        s["elapsedMilliseconds"]=min(3200,i*400)
        s["startedUtc"]=utc(1000+(i-1)*400)
        s["timeoutMilliseconds"]={"info":3000,"versions":3000,"create":45000,
                                 "start":15000,"readiness":12000}[s["stage"]]
    if orig["controlReceipt"]:
        orig["controlReceipt"].update(startedUtc=utc(1000),completedUtc=utc(4200),
                                      attempts=copy.deepcopy(stages))
    for log in orig["logs"]:
        log["observation"]["truncated"]=False
        next(x for x in orig["filesAfter"] if x["file"]==log["file"]).update(
            status="PRESENT",sizeBytes=100,createdUtc=utc(1100),modifiedUtc=utc(4000))
    native={"schema":m.SCHEMA,"arm":arm,"iteration":1,"sourceHeadSha":SOURCE,"workflowSha":WORKFLOW,
            "runId":RUN,"runAttempt":ATTEMPT,"instance":orig["instance"],
            "originalReceiptSha256":None,"startedUtc":utc(0),"completedUtc":utc(6000),
            "bootstrapStartUtc":utc(1000),"bootstrapEndUtc":utc(4400),
            "sectorBefore":vol("NEAREST_EXISTING_ANCESTOR"),"sectorAfter":vol(),
            "sectorErrors":[{"file":f,"status":"OK","bytesRead":100,"tailOnly":False,"pairs":[],
                             "fileLength":100,"createdUtc":utc(1100),"modifiedUtc":utc(4000),"stable":True}
                            for f in sorted(m.LOGS)],
            "monitor":{"status":"COMPLETE","cadenceMs":500,"snapshots":[]}}
    for i,t in enumerate(range(500,5001,500)):
        procs=[] if t<1500 else [{"kind":"LAUNCHER","pid":12,"startedUtc":utc(1200),"cpuMs":i},
                                 {"kind":"ENGINE","pid":13,"startedUtc":utc(1300),"cpuMs":i*2}]
        log={"status":"MISSING","sizeBytes":None,"modifiedUtc":None} if t<1500 else {
            "status":"PRESENT","sizeBytes":i*100,"modifiedUtc":utc(t)}
        native["monitor"]["snapshots"].append({"utc":utc(t),"elapsedMs":t-500,"processes":procs,"processStatus":"OK","vanishedCount":0,"log":log})
    return orig,native


class NativeMatrixTests(unittest.TestCase):
    def setUp(self):
        temp=tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root=Path(temp.name)

    def put(self, arm, orig=None, native=None):
        if orig is None:
            orig,native=fixture(arm)
        path=self.root/f"rel755-{RUN}-{ATTEMPT}-{arm}-1"
        path.mkdir(exist_ok=True)
        raw=json.dumps(orig).encode("utf-8")
        (path/"sample.json").write_bytes(raw)
        native["originalReceiptSha256"]=hashlib.sha256(raw).hexdigest()
        (path/"native.json").write_text(json.dumps(native),encoding="utf-8")
        return path

    def all(self):
        for arm in m.legacy.ARMS:
            self.put(arm)

    def summary(self):
        return m.summarize(self.root,1,IDENTITY)

    def test_exact_plan(self):
        self.assertEqual(3,len(m.legacy.plan(1)))
        self.assertEqual(60,len(m.legacy.plan(20)))
        with self.assertRaises(ValueError):
            m.legacy.plan(2)

    def test_complete_all_ready(self):
        self.all()
        out=self.summary()
        self.assertEqual("COMPLETE_ALL_READY",out["status"])
        self.assertEqual({"READY":3},out["counts"])
        self.assertEqual("NOT_APPLIED",out["remediation"])

    def test_5173_is_not_a_sector_pair(self):
        self.all()
        self.put("win22-auto",*fixture("win22-auto",failed=True))
        out=self.summary()
        self.assertEqual("COMPLETE_WITH_BOOTSTRAP_FAILURES",out["status"])
        self.assertEqual("5173_FILE_INCONSISTENCY_CAUSE_UNRESOLVED",out["rows"][0]["sectorDisposition"])

    def test_large_sector_without_matching_error_does_not_prove_cause(self):
        orig,n=fixture("win22-named",failed=True)
        n["sectorBefore"]=vol("NEAREST_EXISTING_ANCESTOR",8192)
        n["sectorAfter"]=vol(physical=8192)
        self.put("win22-named",orig,n)
        row=self.summary()["rows"][1]
        self.assertEqual("5173_FILE_INCONSISTENCY_CAUSE_UNRESOLVED",row["sectorDisposition"])

    def pair_fixture(self):
        orig,n=fixture("win22-auto",failed=True)
        orig["logs"][0]["observation"]["sqlErrors"]=[{"error":5178,"severity":16,"state":1}]
        next(x for x in orig["filesAfter"] if x["file"]=="master.mdf").update(
            status="PRESENT",sizeBytes=4096,createdUtc=utc(1500),modifiedUtc=utc(1700))
        n["sectorBefore"]=vol("NEAREST_EXISTING_ANCESTOR",8192)
        n["sectorAfter"]=vol(physical=8192)
        log=next(x for x in n["sectorErrors"] if x["file"]==orig["logs"][0]["file"])
        log["pairs"]=[{"error":5178,"severity":16,"state":1,"scope":"OWNED_FILE","file":"master.mdf",
                       "originalBytes":4096,"actualBytes":8192,"headerUtc":utc(1800),
                       "messageUtc":utc(1900),"volume":vol(physical=8192)}]
        return orig,n,log

    def test_exact_correlated_5178(self):
        orig,n,_=self.pair_fixture()
        self.put("win22-auto",orig,n)
        row=self.summary()["rows"][0]
        self.assertEqual("5178_EXACT_FILE_SECTOR_MISMATCH_CORROBORATED",row["sectorDisposition"])
        self.assertTrue(row["sectorPairs"][0]["corroborated"])

    def test_unmatched_volume_tail_and_foreign_file_do_not_corroborate(self):
        for mutation in (
            lambda p,l:p["volume"].update(rootDrive="D:"),
            lambda p,l:p.update(actualBytes=16384),
            lambda p,l:l.update(tailOnly=True),
            lambda p,l:p.update(scope="OUTSIDE_OR_UNKNOWN",file=None,volume=None),
        ):
            orig,n,log=self.pair_fixture()
            mutation(log["pairs"][0],log)
            self.put("win22-auto",orig,n)
            row=self.summary()["rows"][0]
            # A malformed tail mutation is rejected outright; other valid but
            # uncorroborated pairs retain the unresolved disposition.
            if row["status"] == "INCOMPLETE":
                self.assertEqual("LOG_BYTE_ACCOUNTING",row["reason"])
            else:
                self.assertEqual("5178_PAIR_OR_VOLUME_UNRESOLVED",row["sectorDisposition"])

    def test_pair_must_match_original_signature(self):
        orig,n,log=self.pair_fixture()
        log["pairs"][0]["severity"]=17
        self.put("win22-auto",orig,n)
        self.assertEqual("PAIR_SIGNATURE",self.summary()["rows"][0]["reason"])

    def test_timeout_distinguishes_native_exit_observation(self):
        for code in (None,0):
            orig,n=fixture("win22-auto",failed=True)
            orig["bootstrap"][-1].update(status="TIMEOUT",error="NativeProcessTimeout",exitCode=code,
                                          elapsedMilliseconds=3200)
            orig["failure"]="NativeProcessTimeout"
            orig["controlReceipt"].update(failure="NativeProcessTimeout",attempts=copy.deepcopy(orig["bootstrap"]))
            self.put("win22-auto",orig,n)
            t=self.summary()["rows"][0]["progress"]["timeoutStages"][0]
            self.assertEqual("create",t["stage"])
            self.assertEqual("EXIT_CODE_NOT_OBSERVED" if code is None else "EXIT_CODE_RETAINED_AT_TIMEOUT",
                             t["launcherExitObservation"])
            self.assertTrue(t["sessionEngineSeen"])
            self.assertTrue(t["logChangeSeen"])

    def test_monitor_gap_and_missing_volume_fail_closed(self):
        self.all()
        for mutate in (
            lambda n:n["monitor"].update(status="LIMIT_REACHED"),
            lambda n:n["monitor"].update(snapshots=n["monitor"]["snapshots"][:-2]),
            lambda n:n.update(sectorAfter={"status":"SECTOR_QUERY_FAILED"}),
            lambda n:n["sectorAfter"].update(rootDrive="D:"),
            lambda n:n["sectorAfter"].update(volumeIdentitySha256="e"*64),
            lambda n:n["monitor"]["snapshots"][2].update(processStatus="PARTIAL"),
            lambda n:n["monitor"]["snapshots"][2].update(elapsedMs=1501),
            lambda n:n["sectorAfter"]["sector"].update(maxPhysicalBytes=512),
        ):
            orig,n=fixture("win22-auto")
            mutate(n)
            self.put("win22-auto",orig,n)
            self.assertEqual("INCOMPLETE",self.summary()["status"])

    def test_no_corroboration_for_stale_or_partial_pair(self):
        for mutate in (
            lambda n,p,l:p.update(headerUtc=utc(4500),messageUtc=utc(4600)),
            lambda n,p,l:p.update(headerUtc=utc(200),messageUtc=utc(300)),
            lambda n,p,l:l.update(modifiedUtc=utc(4001)),
            lambda n,p,l:n["sectorBefore"].update(volumeIdentitySha256="e"*64),
            lambda n,p,l:n["monitor"].update(status="LIMIT_REACHED"),
            lambda n,p,l:l.update(stable=False),
        ):
            with self.subTest(mutate=mutate):
                orig,n,log=self.pair_fixture()
                mutate(n,log["pairs"][0],log)
                self.put("win22-auto",orig,n)
                row=self.summary()["rows"][0]
                self.assertNotEqual("5178_EXACT_FILE_SECTOR_MISMATCH_CORROBORATED",
                                    row.get("sectorDisposition"))
                self.assertTrue(all(not p["corroborated"] for p in row.get("sectorPairs",[])))

    def test_log_inventory_and_byte_accounting(self):
        self.all()
        for mutate in (
            lambda n:n["sectorErrors"][0].update(bytesRead=103),
            lambda n:n["sectorErrors"][0].update(fileLength=101),
            lambda n:n["sectorErrors"][0].update(status="MISSING",fileLength=None,bytesRead=0,stable=False),
            lambda n:n["sectorErrors"][0].update(status="CHANGED_DURING_READ",stable=False),
            lambda n:n["sectorErrors"][0].update(modifiedUtc=utc(4100)),
        ):
            orig,n=fixture("win22-auto")
            mutate(n)
            self.put("win22-auto",orig,n)
            self.assertEqual("INCOMPLETE",self.summary()["status"])

    def test_stage_and_controller_windows_bound_to_original(self):
        self.all()
        for mutate in (
            lambda o,n:o["controlReceipt"].update(startedUtc=utc(200)),
            lambda o,n:n.update(startedUtc=utc(200)),
            lambda o,n:n.update(bootstrapEndUtc=utc(5400)),
            lambda o,n:o.update(completedUtc=utc(4400)),
            lambda o,n:o["bootstrap"][-1].update(startedUtc=utc(5000)),
            lambda o,n:o["bootstrap"][-1].update(timeoutMilliseconds=45000),
        ):
            orig,n=fixture("win22-auto")
            mutate(orig,n)
            orig["controlReceipt"]["attempts"]=copy.deepcopy(orig["bootstrap"])
            self.put("win22-auto",orig,n)
            self.assertEqual("INCOMPLETE",self.summary()["status"])

    def test_alternative_requires_real_stage_start(self):
        self.all()
        orig,n=fixture("win22-named")
        orig["bootstrap"][0].pop("startedUtc")
        self.put("win22-named",orig,n)
        self.assertEqual("INCOMPLETE",self.summary()["status"])

    def test_stale_source_digest_and_foreign_artifact(self):
        self.all()
        orig,n=fixture("win22-auto")
        n["runAttempt"]="2"
        self.put("win22-auto",orig,n)
        self.assertEqual("NATIVE_IDENTITY",self.summary()["rows"][0]["reason"])
        p=self.put("win22-auto")
        (p/"sample.json").write_bytes((p/"sample.json").read_bytes()+b"\n")
        self.assertEqual("ORIGINAL_RECEIPT_DIGEST",self.summary()["rows"][0]["reason"])
        self.put("win22-auto")
        (self.root/"rel755-foreign").mkdir()
        self.assertEqual("INCOMPLETE",self.summary()["status"])

    def test_guard_only_receipt_is_incomplete(self):
        orig,n=fixture("win25-auto")
        path=self.put("win25-auto",orig,n)
        (path/"sample.json").unlink()
        n.update(guardFailure="OsBuildMismatch",originalReceiptSha256=None)
        (path/"native.json").write_text(json.dumps(n),encoding="utf-8")
        row=self.summary()["rows"][2]
        self.assertEqual("INCOMPLETE",row["status"])
        self.assertEqual("OsBuildMismatch",row["reason"])
        self.assertFalse(row["nativeEvidenceComplete"])

    def test_malformed_pair_path_rejected(self):
        orig,n,log=self.pair_fixture()
        log["pairs"][0]["file"]="../master.mdf"
        self.put("win22-auto",orig,n)
        self.assertEqual("PAIR_FILE",self.summary()["rows"][0]["reason"])


if __name__=="__main__":
    unittest.main()
