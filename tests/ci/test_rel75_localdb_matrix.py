"""Matrix contract regressions; all receipts are synthetic."""
import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0,str(Path(__file__).resolve().parents[2] / "scripts"))
import rel75_localdb_matrix as m

SOURCE, WORKFLOW, RUN, ATTEMPT = "a"*40,"b"*40,"12345","1"


def receipt(arm, iteration=1, state="READY"):
    version = "15.0.4382.1"
    kind = "EXISTING_15_CONTROL" if arm != "win22-named" else "EXPERIMENT_DEFAULT_TOOL"
    stages = [{"stage":"create","attempt":1,"error":"None"}]
    if state == "READY":
        stages += [{"stage":"readiness","attempt":1,"error":"None"}]
    else:
        stages[0]["error"] = "InstanceStartFailed"
    ctl = None if kind != "EXISTING_15_CONTROL" else {
        "schemaVersion":"CI_LOCALDB_BOOTSTRAP_V1","mode":"COLD","instance":"MSSQLLocalDB",
        "sourceHeadSha":SOURCE,"workflowSha":WORKFLOW,"runId":RUN,"runAttempt":ATTEMPT,
        "status":"READY" if state=="READY" else "FAILED","attempts":copy.deepcopy(stages)}
    return {
        "schema":m.SCHEMA,"arm":arm,"iteration":iteration,
        "sourceHeadSha":SOURCE,"workflowSha":WORKFLOW,"runId":RUN,"runAttempt":ATTEMPT,
        "osBuild":m.ARMS[arm][1],"imageVersion":"20261123.376.1",
        "instance":f"REL75_{RUN}_{ATTEMPT}_{iteration}_abcdef012345" if arm=="win22-named" else "MSSQLLocalDB",
        "status":state,"failure":None if state=="READY" else "InstanceStartFailed",
        "coldVerified":True,"bootstrapKind":kind,"selectedVersion":version,
        "engineVersion":version if state=="READY" else None,
        "elapsedMilliseconds":3200,"readinessAttempts":int(state=="READY"),
        "preflight":[{"stage":"versions","attempt":1,"error":"None"}],"bootstrap":stages,"controlReceipt":ctl,
        "filesBefore":[{"file":name,"status":"MISSING"} for name in sorted(m.FILES)],
        "filesAfter":[{"file":name,"status":"MISSING"} for name in sorted(m.FILES)],
        "diskBefore":{"status":"OK","freeBytes":1000,"totalBytes":2000},
        "diskAfter":{"status":"OK","freeBytes":900,"totalBytes":2000},
        "logs":[{"file":name,"observation":{"status":"OK","bytesRead":100,"sqlErrors":[{"error":5173,"severity":16,"state":1}] if state!="READY" else []}} for name in ("error.log","error1.log","error2.log")],
        "applicationEvents":{"status":"OK","events":[{"id":528}] if state!="READY" else []},
    }


class MatrixTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def put(self, arm, value=None, iteration=1):
        p = self.root / f"rel75-{RUN}-{ATTEMPT}-{arm}-{iteration}" / "sample.json"
        p.parent.mkdir(exist_ok=True)
        p.write_text(json.dumps(receipt(arm,iteration) if value is None else value),encoding="utf-8")
        return p

    def all(self):
        for arm in m.ARMS:
            self.put(arm)

    def summary(self):
        return m.summarize(self.root,1,SOURCE,WORKFLOW,RUN,ATTEMPT)

    def test_exact_plan(self):
        self.assertEqual(3,len(m.plan(1)))
        large=m.plan(20)
        self.assertEqual(60,len(large))
        self.assertEqual(60,len({(x["arm"],x["iteration"]) for x in large}))
        for n in (0,2,21):
            with self.assertRaises(ValueError):
                m.plan(n)

    def test_all_ready(self):
        self.all()
        result=self.summary()
        self.assertEqual("COMPLETE_ALL_READY",result["status"])
        self.assertEqual(3,result["observed"])

    def test_negative_observation_is_not_lost(self):
        self.all()
        self.put("win22-auto",receipt("win22-auto",state="BOOTSTRAP_FAILED"))
        result=self.summary()
        self.assertEqual("COMPLETE_WITH_BOOTSTRAP_FAILURES",result["status"])
        self.assertEqual(1,result["arms"][0]["bootstrapFailed"])
        self.assertEqual([[5173,16,1]],result["observations"][0]["sqlSignatures"])

    def test_missing_and_stale_attempt(self):
        self.put("win22-auto")
        old=receipt("win22-named")
        old["runAttempt"]="2"
        self.put("win22-named",old)
        result=self.summary()
        self.assertEqual("INCOMPLETE",result["status"])
        self.assertEqual(1,len(result["missing"]))
        self.assertEqual(1,len(result["invalid"]))

    def test_wrong_source_and_workflow(self):
        self.all()
        for key in ("sourceHeadSha","workflowSha"):
            bad=receipt("win22-named")
            bad[key]="c"*40
            self.put("win22-named",bad)
            self.assertEqual(1,len(self.summary()["invalid"]))

    def test_wrong_target(self):
        self.all()
        bad=receipt("win22-named")
        bad["instance"]="MSSQLLocalDB"
        self.put("win22-named",bad)
        self.assertEqual(1,len(self.summary()["invalid"]))

    def test_no_create_retry(self):
        self.all()
        bad=receipt("win22-named")
        bad["bootstrap"].insert(1,bad["bootstrap"][0])
        self.put("win22-named",bad)
        self.assertEqual(1,len(self.summary()["invalid"]))

    def test_no_false_ready(self):
        self.all()
        bad=receipt("win22-auto")
        bad["controlReceipt"]["status"]="FAILED"
        self.put("win22-auto",bad)
        self.assertEqual(1,len(self.summary()["invalid"]))

    def test_partial_diagnostics(self):
        self.all()
        bad=receipt("win25-auto")
        bad["logs"]=[]
        self.put("win25-auto",bad)
        self.assertEqual("INCOMPLETE",self.summary()["status"])
        self.assertEqual(1,self.summary()["arms"][2]["diagnosticsPartial"])

    def test_diagnostic_access_denial_is_incomplete(self):
        self.all()
        bad=receipt("win25-auto")
        bad["filesAfter"][0]["status"]="ACCESS_DENIED"
        self.put("win25-auto",bad)
        self.assertEqual("INCOMPLETE",self.summary()["status"])
        bad=receipt("win25-auto")
        bad["logs"][0]["observation"]["status"]="READ_FAILED"
        self.put("win25-auto",bad)
        self.assertEqual("INCOMPLETE",self.summary()["status"])

    def test_failed_create_cannot_be_promoted(self):
        self.all()
        bad=receipt("win22-named")
        bad["bootstrap"][0]["error"]="InstanceStartFailed"
        self.put("win22-named",bad)
        self.assertEqual(1,len(self.summary()["invalid"]))

    def test_duplicate_json_keys(self):
        self.all()
        p=self.put("win22-auto")
        p.write_text('{"schema":"a","schema":"b"}',encoding="utf-8")
        self.assertEqual(1,len(self.summary()["invalid"]))

    def test_no_unexpected_artifacts(self):
        self.all()
        (self.root/"rel75-12345-2-win22-auto-1").mkdir()
        self.assertEqual("INCOMPLETE",self.summary()["status"])
        self.assertEqual(1,self.summary()["unexpectedArtifacts"])

    def test_unknown_file_name(self):
        self.all()
        bad=receipt("win22-named")
        bad["filesAfter"][0]["file"]="../secret"
        self.put("win22-named",bad)
        self.assertEqual(1,len(self.summary()["invalid"]))

    def test_not_cold_is_not_success(self):
        self.all()
        bad=receipt("win22-named")
        bad.update(status="NOT_COLD",failure="NotCold",coldVerified=False,bootstrap=[],readinessAttempts=0)
        self.put("win22-named",bad)
        self.assertEqual("INCOMPLETE",self.summary()["status"])
        self.assertEqual(1,self.summary()["arms"][1]["notCold"])


if __name__ == "__main__":
    unittest.main()
