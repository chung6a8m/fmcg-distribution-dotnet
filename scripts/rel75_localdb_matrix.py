"""REL-75.3 investigation receipts; no application/CI acceptance claims."""
from __future__ import annotations

import argparse
import collections
import json
import os
import re
import sys
from pathlib import Path

ARMS = {"win22-auto": ("windows-2022", 20348),
        "win22-named": ("windows-2022", 20348),
        "win25-auto": ("windows-2025", 26100)}
SCHEMA = "REL75_LOCALDB_SAMPLE_V1"
SHA = re.compile(r"[a-f0-9]{40}")
VERSION = re.compile(r"(15|16|17)\.0\.\d+\.\d+")
IMAGE = re.compile(r"[0-9.]{1,32}")
LABEL = re.compile(r"[A-Za-z][A-Za-z0-9_]{0,63}")
STATES = {"READY", "BOOTSTRAP_FAILED", "NOT_COLD", "NOT_EXECUTED"}
FILES = {"master.mdf", "mastlog.ldf", "model.mdf", "modellog.ldf", "msdbdata.mdf",
         "msdblog.ldf", "model_msdbdata.mdf", "model_msdblog.ldf",
         "model_replicatedmaster.mdf", "model_replicatedmaster.ldf",
         "error.log", "error1.log", "error2.log"}


def plan(samples: int) -> list[dict]:
    if samples not in (1, 20):
        raise ValueError("only 1 or 20 samples per arm")
    return [{"arm": arm, "os": os_name, "iteration": i}
            for arm, (os_name, _) in ARMS.items() for i in range(1, samples + 1)]


def reject_duplicates(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key")
        result[key] = value
    return result


def read_receipt(path: Path) -> dict:
    if path.is_symlink() or not path.is_file() or path.stat().st_size > 256 * 1024:
        raise ValueError("unsafe receipt")
    with path.open(encoding="utf-8") as file:
        value = json.load(file, object_pairs_hook=reject_duplicates,
                          parse_constant=lambda _: (_ for _ in ()).throw(ValueError("nonfinite JSON")))
    if not isinstance(value, dict):
        raise ValueError("not an object")
    return value


def integer(value, low=0, high=2**63-1):
    return type(value) is int and low <= value <= high


def require_native(stage, name, attempt=1, error="None"):
    # An expected absence/readiness timeout may be reported by the native tool
    # with either zero or nonzero exit. It is not a general success exception.
    allowed_status = {"OK","COMMAND_FAILED"} if error != "None" else {"OK"}
    if (stage["stage"] != name or stage["attempt"] != attempt or stage["error"] != error
            or stage.get("status") not in allowed_status or stage.get("truncated") is not False
            or stage.get("deadlineExpired",False) is not False
            or not integer(stage.get("exitCode"),-2**31,2**31-1)
            or (stage["status"] == "OK") != (stage["exitCode"] == 0)):
        raise ValueError("native stage evidence mismatch")


def validate_ready_sequence(stages, control, attempts):
    # Match the actual helper, including its one expected cold absence before
    # versions/create, and the alternative's shorter preflight-separated path.
    index = 0

    def take(name, attempt=1, error="None"):
        nonlocal index
        if index >= len(stages):
            raise ValueError("missing bootstrap stage")
        require_native(stages[index],name,attempt,error)
        index += 1

    if control:
        take("info",error="AutomaticInstanceNotCreated")
        take("versions")
    take("create")
    take("info")
    if index < len(stages) and stages[index]["stage"] == "start":
        take("start")
        take("info")
    if not 1 <= attempts <= 3:
        raise ValueError("invalid ready attempt count")
    for i in range(1,attempts+1):
        take("readiness",i,"None" if i == attempts else "LoginTimeout")
        if i < attempts:
            # Confirm Running on both sides of the bounded backoff.
            take("info",i)
            take("info",i)
    if index != len(stages):
        raise ValueError("extra bootstrap stage")


def validate(receipt, arm, iteration, source, workflow, run, attempt):
    if (receipt.get("schema"), receipt.get("arm"), receipt.get("iteration"),
        receipt.get("sourceHeadSha"), receipt.get("workflowSha"),
        receipt.get("runId"), receipt.get("runAttempt"), receipt.get("osBuild")) != (
        SCHEMA, arm, iteration, source, workflow, run, attempt, ARMS[arm][1]):
        raise ValueError("identity mismatch")
    if not integer(receipt["iteration"], 1, 20):
        raise ValueError("invalid iteration")
    target = receipt.get("instance")
    if arm == "win22-named":
        if not isinstance(target, str) or not re.fullmatch(
            rf"REL75_{run}_{attempt}_{iteration}_[a-f0-9]{{12}}", target
        ):
            raise ValueError("wrong named target")
    elif target != "MSSQLLocalDB":
        raise ValueError("wrong automatic target")
    image = receipt.get("imageVersion")
    if not isinstance(image, str) or not IMAGE.fullmatch(image):
        raise ValueError("missing image version")
    state, reason = receipt.get("status"), receipt.get("failure")
    if state not in STATES or (state == "READY" and reason is not None):
        raise ValueError("invalid result")
    if state != "READY" and (not isinstance(reason, str) or not LABEL.fullmatch(reason)):
        raise ValueError("invalid failure category")
    if not integer(receipt.get("elapsedMilliseconds"), 0, 65000):
        raise ValueError("invalid bootstrap duration")
    if not integer(receipt.get("readinessAttempts"), 0, 3):
        raise ValueError("invalid readiness attempts")
    preflight, stages = receipt.get("preflight"), receipt.get("bootstrap")
    if not isinstance(preflight, list) or not isinstance(stages, list):
        raise ValueError("missing stage evidence")
    if len(preflight) > 8 or len(stages) > 24:
        raise ValueError("excess stage evidence")
    for stage in preflight + stages:
        if not isinstance(stage, dict) or stage.get("stage") not in {"list","info","versions","create","start","readiness"}:
            raise ValueError("invalid stage")
        if not integer(stage.get("attempt"), 1, 3) or not isinstance(stage.get("error"), str) or not LABEL.fullmatch(stage["error"]):
            raise ValueError("invalid stage classification")
    if any(s["stage"] in {"create","start","readiness"} for s in preflight):
        raise ValueError("mutation during preflight")
    creates = [s for s in stages if s["stage"] == "create"]
    starts = [s for s in stages if s["stage"] == "start"]
    if len(creates) > 1 or len(starts) > 1:
        raise ValueError("unexpected create/start retry")
    if state in {"READY","BOOTSTRAP_FAILED"}:
        ver = receipt.get("selectedVersion")
        if receipt.get("coldVerified") is not True or len(creates) != 1:
            raise ValueError("not a cold execution")
        if not isinstance(ver, str) or not VERSION.fullmatch(ver) or (
            arm != "win25-auto" and not ver.startswith("15.0.")
        ):
            raise ValueError("wrong runtime")
        if len(preflight) != 2:
            raise ValueError("missing cold preflight")
        require_native(preflight[0],"versions")
        if arm == "win22-named":
            require_native(preflight[1],"list")
        else:
            require_native(preflight[1],"info",error="AutomaticInstanceNotCreated")
        if receipt.get("installedVersions") != [ver]:
            raise ValueError("runtime inventory mismatch")
        tools = receipt.get("tools")
        if not isinstance(tools,dict) or any(
            not isinstance(tools.get(key),dict) or tools[key].get("status") != "OK"
            for key in ("localDb","sqlcmd")
        ):
            raise ValueError("tool evidence missing")
        version = tools["localDb"].get("version")
        if not isinstance(version,str):
            raise ValueError("tool version missing")
        for prefix,family in (("2019.150.","15.0."),("2022.160.","16.0."),("2025.170.","17.0.")):
            if version.startswith(prefix):
                version = family + version[len(prefix):]
                break
        if version != ver:
            raise ValueError("tool/runtime mismatch")
        before = receipt.get("filesBefore")
        if not isinstance(before,list) or len(before) != len(FILES) or any(
            f.get("status") != "MISSING" for f in before
        ) or {f.get("file") for f in before} != FILES:
            raise ValueError("cold filesystem evidence mismatch")
        kind = receipt.get("bootstrapKind")
        expected_kind = ("EXISTING_15_CONTROL" if arm != "win22-named" and ver.startswith("15.0.")
                         else "EXPERIMENT_DEFAULT_TOOL")
        if kind != expected_kind:
            raise ValueError("wrong bootstrap implementation")
        if kind == "EXISTING_15_CONTROL":
            ctl = receipt.get("controlReceipt")
            if not isinstance(ctl, dict) or any(ctl.get(k) != v for k, v in {
                "schemaVersion":"CI_LOCALDB_BOOTSTRAP_V1", "mode":"COLD", "instance":"MSSQLLocalDB",
                "sourceHeadSha":source, "workflowSha":workflow, "runId":run, "runAttempt":attempt
            }.items()) or ctl.get("attempts") != stages:
                raise ValueError("invalid control receipt")
            if any(ctl.get(k) != receipt.get(k) for k in
                   ("selectedVersion","installedVersions","engineVersion","readinessAttempts","elapsedMilliseconds","failure","imageVersion")):
                raise ValueError("control evidence mismatch")
            if ctl.get("status") != ("READY" if state == "READY" else "FAILED"):
                raise ValueError("control status mismatch")
        elif receipt.get("controlReceipt") is not None:
            raise ValueError("unexpected control receipt")
        if state == "READY":
            if receipt.get("engineVersion") != ver:
                raise ValueError("readiness version mismatch")
            validate_ready_sequence(stages,kind == "EXISTING_15_CONTROL",receipt["readinessAttempts"])
    elif creates and state == "NOT_EXECUTED":
        raise ValueError("create evidence incorrectly classified")
    # Project only fixed categories/numeric metadata into the cross-run report.
    logs = receipt.get("logs")
    events = receipt.get("applicationEvents")
    codes, event_ids = set(), set()
    if not isinstance(logs, list) or len(logs) > 3 or not isinstance(events, dict):
        raise ValueError("invalid diagnostics")
    for log in logs:
        if log.get("file") not in {"error.log","error1.log","error2.log"}:
            raise ValueError("unknown log")
        observation = log.get("observation", {})
        errors = observation.get("sqlErrors", [])
        if not isinstance(errors, list) or len(errors) > 16 or not integer(observation.get("bytesRead"),0,65536):
            raise ValueError("invalid error log evidence")
        for e in errors:
            if not integer(e.get("error"),0,2**31-1) or not integer(e.get("severity"),0,255) or not integer(e.get("state"),0,255):
                raise ValueError("invalid numeric signature")
            codes.add((e["error"],e["severity"],e["state"]))
    records = events.get("events")
    if not isinstance(records,list) or len(records)>8:
        raise ValueError("invalid events")
    for event in records:
        if not integer(event.get("id"),0,2**31-1):
            raise ValueError("invalid event id")
        event_ids.add(event["id"])
    diagnostics_complete = True
    for key in ("filesBefore","filesAfter"):
        files = receipt.get(key)
        if not isinstance(files,list) or len(files) > len(FILES):
            raise ValueError("invalid file inventory")
        if any(f.get("file") not in FILES for f in files) or len({f["file"] for f in files}) != len(files):
            raise ValueError("invalid filename")
        diagnostics_complete &= len(files) == len(FILES) and all(
            f.get("status") == "MISSING" or (f.get("status") == "PRESENT" and integer(f.get("sizeBytes"))
            and isinstance(f.get("createdUtc"),str) and isinstance(f.get("modifiedUtc"),str)) for f in files)
    diagnostics_complete &= len(logs) == 3 and len({log["file"] for log in logs}) == 3 and all(
        log.get("observation",{}).get("status") in {"OK","MISSING"} for log in logs)
    diagnostics_complete &= events.get("status") in {"OK","NO_MATCH"}
    for key in ("diskBefore","diskAfter"):
        disk = receipt.get(key)
        diagnostics_complete &= isinstance(disk,dict) and disk.get("status") == "OK" and integer(disk.get("freeBytes")) and integer(disk.get("totalBytes"))
    return {"arm":arm, "iteration":iteration, "status":state, "failure":reason,
            "imageVersion":image, "selectedVersion":receipt.get("selectedVersion") if
            isinstance(receipt.get("selectedVersion"),str) and VERSION.fullmatch(receipt["selectedVersion"]) else None,
            "elapsedMilliseconds":receipt["elapsedMilliseconds"], "readinessAttempts":receipt["readinessAttempts"],
            "diagnosticsComplete":bool(diagnostics_complete), "sqlSignatures":[list(c) for c in sorted(codes)],
            "eventIds":sorted(event_ids)}


def summarize(root: Path, samples: int, source: str, workflow: str, run: str, attempt: str):
    if not SHA.fullmatch(source) or not SHA.fullmatch(workflow) or not re.fullmatch(r"\d{1,20}",run) or not re.fullmatch(r"\d{1,10}",attempt):
        raise ValueError("invalid run context")
    expected = {f"rel75-{run}-{attempt}-{p['arm']}-{p['iteration']}":p for p in plan(samples)}
    observations, missing, invalid = [], [], []
    unexpected = sum(1 for p in root.iterdir() if p.name not in expected) if root.is_dir() else 0
    for name,p in expected.items():
        directory = root / name
        path = directory / "sample.json"
        cell = {"arm":p["arm"], "iteration":p["iteration"]}
        if not directory.exists():
            missing.append(cell)
            continue
        try:
            if directory.is_symlink() or set(x.name for x in directory.iterdir()) != {"sample.json"}:
                raise ValueError("unexpected artifact layout")
            observations.append(validate(read_receipt(path),p["arm"],p["iteration"],source,workflow,run,attempt))
        except (OSError,ValueError,KeyError,TypeError,AttributeError):
            invalid.append(cell)
    rows = []
    for arm in ARMS:
        obs = [o for o in observations if o["arm"] == arm]
        c = collections.Counter(o["status"] for o in obs)
        groups = collections.Counter((o["imageVersion"],o["selectedVersion"],o["status"]) for o in obs)
        rows.append({"arm":arm, "expected":samples, "recorded":len(obs),
                     "ready":c["READY"], "bootstrapFailed":c["BOOTSTRAP_FAILED"],
                     "notCold":c["NOT_COLD"], "notExecuted":c["NOT_EXECUTED"],
                     "coldExecuted":c["READY"]+c["BOOTSTRAP_FAILED"],
                     "diagnosticsPartial":sum(not o["diagnosticsComplete"] for o in obs),
                     "strata":[{"imageVersion":k[0],"selectedVersion":k[1],"status":k[2],"count":v}
                               for k,v in sorted(groups.items(),key=lambda kv:str(kv[0]))]})
    complete = not missing and not invalid and not unexpected
    executed = sum(row["coldExecuted"] for row in rows)
    failed = sum(row["bootstrapFailed"] for row in rows)
    partial = sum(row["diagnosticsPartial"] for row in rows)
    status = ("INCOMPLETE" if not complete or executed != len(expected) or partial else
              "COMPLETE_WITH_BOOTSTRAP_FAILURES" if failed else "COMPLETE_ALL_READY")
    return {"schema":"REL75_LOCALDB_MATRIX_V1", "scope":"INVESTIGATION_ONLY",
            "sourceHeadSha":source,"workflowSha":workflow,"runId":run,"runAttempt":attempt,
            "samplesPerArm":samples,"status":status,"expected":len(expected),"observed":len(observations),
            "missing":missing,"invalid":invalid,"unexpectedArtifacts":unexpected,
            "arms":rows,"observations":observations}


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command",required=True)
    p = sub.add_parser("plan"); p.add_argument("--samples",type=int,required=True)
    s = sub.add_parser("summarize"); s.add_argument("--samples",type=int,required=True)
    s.add_argument("--root",type=Path,required=True); s.add_argument("--output",type=Path,required=True)
    args = parser.parse_args()
    if args.command == "plan":
        print(json.dumps({"include":plan(args.samples)},separators=(",",":")))
        return 0
    result = summarize(args.root,args.samples,os.environ["REL75_SOURCE_SHA"],os.environ["GITHUB_SHA"],
                       os.environ["GITHUB_RUN_ID"],os.environ["GITHUB_RUN_ATTEMPT"])
    args.output.parent.mkdir(parents=True,exist_ok=True)
    with args.output.open("x",encoding="utf-8") as out:
        json.dump(result,out,indent=2); out.write("\n")
    print(f'{result["status"]}: {result["observed"]}/{result["expected"]} receipts; investigation only')
    for row in result["arms"]:
        print(f'{row["arm"]}: ready={row["ready"]}, failed={row["bootstrapFailed"]}, notCold={row["notCold"]}, notExecuted={row["notExecuted"]}, partial={row["diagnosticsPartial"]}')
    return 0 if result["status"] == "COMPLETE_ALL_READY" else 1


if __name__ == "__main__":
    sys.exit(main())
