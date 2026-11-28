"""REL-75.5 evidence-only investigation; does not select or apply remediation."""
from __future__ import annotations

import argparse
import collections
import hashlib
import json
import os
import re
from datetime import datetime
from pathlib import Path

import rel75_localdb_matrix as legacy

SCHEMA = "REL755_NATIVE_EVIDENCE_V1"
LOGS = {"error.log", "error1.log", "error2.log"}
SECTOR_KEYS = ("logicalBytes", "physicalAtomicityBytes",
               "physicalPerformanceBytes", "effectiveAtomicityBytes")
MISSING_VOLUME = {"UNAVAILABLE", "UNSUPPORTED_PATH", "UNSUPPORTED_VOLUME",
                  "REPARSE_POINT", "ACCESS_DENIED", "SECTOR_QUERY_FAILED",
                  "MALFORMED_OR_UNSUPPORTED", "MISSING"}


def require(test, code):
    if not test:
        raise ValueError(code)


def positive_int(value, low=1, high=2**63-1):
    return type(value) is int and low <= value <= high


def sector_bytes(n):
    return positive_int(n, 512, 1048576) and n & (n - 1) == 0


def time_ms(value):
    require(isinstance(value, str) and len(value) <= 40, "TIME_FORMAT")
    dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
    require(dt.tzinfo is not None and dt.utcoffset().total_seconds() == 0, "TIME_NOT_UTC")
    return int(dt.timestamp() * 1000)


def read_native(path):
    require(path.is_file() and not path.is_symlink() and path.stat().st_size <= 524288,
            "UNSAFE_NATIVE_RECEIPT")
    with path.open(encoding="utf-8") as handle:
        value = json.load(handle, object_pairs_hook=legacy.reject_duplicates,
                          parse_constant=lambda _: (_ for _ in ()).throw(ValueError("NONFINITE")))
    require(isinstance(value, dict), "NATIVE_NOT_OBJECT")
    return value


def volume(v, *, before=False):
    if v is None:
        return None
    require(isinstance(v, dict), "VOLUME_TYPE")
    if v["status"] != "OK":
        require(v["status"] in MISSING_VOLUME, "VOLUME_STATUS")
        return None
    require(isinstance(v["rootDrive"], str) and re.fullmatch(r"[A-Z]:", v["rootDrive"]),
            "VOLUME_DRIVE")
    require(v["resolution"] in ("EXISTING_PATH", "NEAREST_EXISTING_ANCESTOR")
            and (not before or v["resolution"] == "NEAREST_EXISTING_ANCESTOR"), "VOLUME_RESOLUTION")
    s = v["sector"]
    require(s["status"] == "OK" and all(sector_bytes(s[k]) for k in SECTOR_KEYS), "SECTOR_FIELDS")
    logical, atomic, performance, effective = (s[k] for k in SECTOR_KEYS)
    require(logical <= atomic and logical <= performance and logical <= effective <= atomic
            and s["maxPhysicalBytes"] == max(atomic, performance), "SECTOR_INCONSISTENT")
    return {"rootDrive": v["rootDrive"], "resolution": v["resolution"],
            **{k: s[k] for k in (*SECTOR_KEYS, "maxPhysicalBytes")}}


def monitor(receipt, native):
    m = native["monitor"]
    require(isinstance(m, dict) and m["cadenceMs"] == 500, "MONITOR_SCHEMA")
    status = m["status"]
    require(status in ("COMPLETE", "NOT_STARTED", "LIMIT_REACHED", "MONITOR_INCOMPLETE",
                       "MONITOR_FAILED", "INVALID_OUTPUT"), "MONITOR_STATUS")
    samples = m["snapshots"]
    require(isinstance(samples, list) and len(samples) <= 170, "MONITOR_LIMIT")
    first, last = None, None
    cpu = {}
    previous_utc, previous_elapsed = None, -1
    largest_gap = 0
    for x in samples:
        utc = time_ms(x["utc"])
        elapsed = x["elapsedMs"]
        require(positive_int(elapsed, 0, 90000) and elapsed > previous_elapsed, "MONITOR_ELAPSED")
        if previous_utc is not None:
            require(utc >= previous_utc, "MONITOR_UTC_ORDER")
            largest_gap = max(largest_gap, utc - previous_utc)
        first = utc if first is None else first
        last, previous_utc, previous_elapsed = utc, utc, elapsed
        rows = x["processes"]
        require(isinstance(rows, list) and len(rows) <= 16, "MONITOR_PROCESS_COUNT")
        ids = set()
        for p in rows:
            start = time_ms(p["startedUtc"])
            require(p["kind"] in ("ENGINE", "LAUNCHER") and positive_int(p["pid"], 1, 2**31-1)
                    and start <= utc and positive_int(p["cpuMs"], 0), "MONITOR_PROCESS")
            key = (p["kind"], p["pid"], start)
            require(key not in ids and p["cpuMs"] >= cpu.get(key, 0), "MONITOR_PROCESS_IDENTITY")
            ids.add(key)
            cpu[key] = p["cpuMs"]
        log = x["log"]
        require(log["status"] in ("MISSING", "PRESENT", "UNAVAILABLE", "ACCESS_DENIED",
                                 "REPARSE_POINT", "UNEXPECTED_FILE_TYPE"), "MONITOR_LOG_STATUS")
        if log["status"] == "PRESENT":
            require(positive_int(log["sizeBytes"], 0) and time_ms(log["modifiedUtc"]) <= utc,
                    "MONITOR_LOG_METADATA")
    if native["bootstrapStartUtc"] is None or native["bootstrapEndUtc"] is None:
        return {"complete": False, "status": status, "count": len(samples), "largestGapMs": largest_gap,
                "timeoutStages": []}
    begin, end = time_ms(native["bootstrapStartUtc"]), time_ms(native["bootstrapEndUtc"])
    require(begin <= end and 0 <= end-begin <= 70000, "OBSERVATION_WINDOW")
    complete = (status == "COMPLETE" and len(samples) >= 2 and first <= begin <= end <= last
                and largest_gap <= 3000 and end-begin >= receipt["elapsedMilliseconds"]-1000
                and samples[0]["log"]["status"] == "MISSING"
                and all(x["log"]["status"] in ("PRESENT","MISSING") for x in samples))
    # Correlate session observations with receipt stages, never attribute an
    # unrelated process to a specific launcher or infer a pipe-handle cause.
    baseline = {(p["kind"], p["pid"], p["startedUtc"]) for p in samples[0]["processes"]} if samples else set()
    stages = receipt["bootstrap"]
    ctl = receipt.get("controlReceipt")
    anchor = time_ms(ctl["startedUtc"]) if ctl else begin
    timeouts = []
    prev = 0
    for stage in stages:
        elapsed = stage["elapsedMilliseconds"]
        require(positive_int(elapsed, 0, 65000) and elapsed >= prev, "STAGE_ELAPSED_ORDER")
        stop = anchor + elapsed
        start = time_ms(stage["startedUtc"]) if "startedUtc" in stage else anchor + prev
        require(start <= stop+250, "STAGE_TIME_WINDOW")
        if stage["stage"] in ("create", "start") and (stage["status"] == "TIMEOUT" or stage["error"] == "NativeProcessTimeout"):
            require(stage["status"] == "TIMEOUT" and stage["error"] == "NativeProcessTimeout",
                    "TIMEOUT_STAGE_MISMATCH")
            inside = [x for x in samples if start <= time_ms(x["utc"]) <= stop]
            complete = complete and bool(inside)
            new = [(p["kind"], p["pid"], p["startedUtc"], p["cpuMs"])
                   for x in inside for p in x["processes"]
                   if (p["kind"], p["pid"], p["startedUtc"]) not in baseline
                   and time_ms(p["startedUtc"]) >= begin]
            log_values = [(x["log"]["sizeBytes"], x["log"]["modifiedUtc"]) for x in inside
                          if x["log"]["status"] == "PRESENT"]
            engine = any(p[0] == "ENGINE" for p in new)
            launcher = any(p[0] == "LAUNCHER" for p in new)
            exit_code = stage["exitCode"]
            require(exit_code is None or positive_int(exit_code, -2**31, 2**31-1), "TIMEOUT_EXIT")
            timeouts.append({"stage": stage["stage"], "nativeExitCode": exit_code,
                             "launcherExitObservation": "EXIT_CODE_RETAINED_AT_TIMEOUT" if exit_code is not None
                             else "EXIT_CODE_NOT_OBSERVED",
                             "sessionLauncherSeen": launcher, "sessionEngineSeen": engine,
                             "logSeen": bool(log_values), "logChangeSeen": len(set(log_values)) > 1,
                             "samplesInWindow": len(inside), "windowBasis": "CONTROL_STARTED_UTC" if ctl
                             else "ALTERNATIVE_ELAPSED_APPROXIMATION",
                             "scope": "SESSION_CORRELATION_NOT_CAUSAL_PROOF"})
        prev = elapsed
    return {"complete": bool(complete), "status": status, "count": len(samples), "largestGapMs": largest_gap,
            "timeoutStages": timeouts}


def sector_pairs(original, native, after):
    logs = native["sectorErrors"]
    require(isinstance(logs, list) and len(logs) <= 3, "SECTOR_LOG_COUNT")
    seen = set()
    findings = []
    partial = len(logs) != 3
    original_logs = {x["file"]: x["observation"] for x in original["logs"]}
    present_files = {x["file"] for x in original["filesAfter"] if x["status"] == "PRESENT"}
    for log in logs:
        filename = log["file"]
        require(filename in LOGS and filename not in seen and positive_int(log["bytesRead"], 0, 65536)
                and type(log["tailOnly"]) is bool, "SECTOR_LOG_SCOPE")
        seen.add(filename)
        require(log["status"] in ("OK", "MISSING", "TRUNCATED", "UNAVAILABLE", "ACCESS_DENIED",
                                  "REPARSE_POINT", "MALFORMED_CONTENT", "READ_FAILED"), "SECTOR_LOG_STATUS")
        partial |= log["status"] not in ("OK", "MISSING")
        pairs = log["pairs"]
        require(isinstance(pairs, list) and len(pairs) <= 16, "PAIR_COUNT")
        count = collections.Counter((e["error"],e["severity"],e["state"])
                                    for e in original_logs.get(filename, {}).get("sqlErrors", []))
        for p in pairs:
            signature = (p["error"],p["severity"],p["state"])
            require(signature[0] == 5178 and count[signature] > 0
                    and sector_bytes(p["originalBytes"]) and sector_bytes(p["actualBytes"]), "PAIR_SIGNATURE")
            count[signature] -= 1
            require(p["scope"] in ("OWNED_FILE", "OUTSIDE_OR_UNKNOWN"), "PAIR_SCOPE")
            require((p["file"] in legacy.FILES-LOGS) if p["scope"] == "OWNED_FILE"
                    else (p["file"] is None and p["volume"] is None), "PAIR_FILE")
            file_volume = volume(p["volume"])
            # Candidate only: exact file present, matched volume, and original
            # message's current sector agrees with independently queried value.
            corroborated = bool(file_volume and after and p["scope"] == "OWNED_FILE"
                                and file_volume["resolution"] == "EXISTING_PATH"
                                and file_volume["rootDrive"] == after["rootDrive"]
                                and all(file_volume[k] == after[k] for k in SECTOR_KEYS)
                                and p["actualBytes"] in (file_volume["physicalAtomicityBytes"],
                                                         file_volume["physicalPerformanceBytes"],
                                                         file_volume["effectiveAtomicityBytes"])
                                and p["file"] in present_files and p["actualBytes"] > p["originalBytes"]
                                and log["status"] == "OK" and not log["tailOnly"])
            findings.append({"error": 5178, "file": p["file"], "scope": p["scope"],
                             "originalBytes": p["originalBytes"], "actualBytes": p["actualBytes"],
                             "volume": file_volume, "corroborated": corroborated})
    return findings, not partial


def inspect_cell(path, arm, iteration, identity):
    require(path.is_dir() and not path.is_symlink() and {p.name for p in path.iterdir()} ==
            {"sample.json","native.json"}, "ARTIFACT_LAYOUT")
    source, workflow, run, attempt = identity
    original_path = path / "sample.json"
    original = legacy.read_receipt(original_path)
    row = legacy.validate(original, arm, iteration, source, workflow, run, attempt)
    native = read_native(path / "native.json")
    require((native["schema"],native["arm"],native["iteration"],native["sourceHeadSha"],
             native["workflowSha"],native["runId"],native["runAttempt"]) ==
            (SCHEMA,arm,iteration,source,workflow,run,attempt), "NATIVE_IDENTITY")
    require(native["originalReceiptSha256"] == hashlib.sha256(original_path.read_bytes()).hexdigest(),
            "ORIGINAL_RECEIPT_DIGEST")
    require(native["instance"] == original["instance"] or
            (native["instance"] is None and row["status"] in ("NOT_COLD","NOT_EXECUTED")), "NATIVE_INSTANCE")
    require(time_ms(native["startedUtc"]) <= time_ms(native["completedUtc"]), "NATIVE_TIME")
    before, after = volume(native["sectorBefore"], before=True), volume(native["sectorAfter"])
    progress = monitor(original, native)
    pairs, logs_complete = sector_pairs(original, native, after)
    complete = (row["diagnosticsComplete"] and before is not None and after is not None
                and before["rootDrive"] == after["rootDrive"] and all(before[k] == after[k] for k in SECTOR_KEYS)
                and progress["complete"] and logs_complete)
    row.update(sectorBefore=before, sectorAfter=after, progress=progress,
               sectorPairs=pairs, nativeEvidenceComplete=bool(complete))
    if any(p["corroborated"] for p in pairs):
        row["sectorDisposition"] = "5178_EXACT_FILE_SECTOR_MISMATCH_CORROBORATED"
    elif any(s[0] == 5178 for s in row["sqlSignatures"]):
        row["sectorDisposition"] = "5178_PAIR_OR_VOLUME_UNRESOLVED"
    elif any(s[0] == 5173 for s in row["sqlSignatures"]):
        row["sectorDisposition"] = "5173_FILE_INCONSISTENCY_CAUSE_UNRESOLVED"
    elif before and before["maxPhysicalBytes"] > 4096:
        row["sectorDisposition"] = "PHYSICAL_GT_4K_WITHOUT_MATCHED_5178"
    else:
        row["sectorDisposition"] = "NO_MATCHED_SECTOR_FAILURE" if before else "GEOMETRY_UNAVAILABLE"
    return row


def summarize(root, samples, identity):
    source, workflow, run, attempt = identity
    require(legacy.SHA.fullmatch(source) and legacy.SHA.fullmatch(workflow)
            and re.fullmatch(r"\d{1,20}",run) and re.fullmatch(r"\d{1,10}",attempt), "RUN_CONTEXT")
    expected = {f"rel755-{run}-{attempt}-{p['arm']}-{p['iteration']}":p for p in legacy.plan(samples)}
    unexpected = sum(1 for p in root.iterdir() if p.name not in expected) if root.is_dir() else 0
    rows=[]
    for name,p in expected.items():
        try:
            rows.append(inspect_cell(root/name,p["arm"],p["iteration"],identity))
        except (OSError,ValueError,KeyError,TypeError,AttributeError,IndexError,OverflowError,RecursionError) as ex:
            code=str(ex)
            rows.append({"arm":p["arm"],"iteration":p["iteration"],"status":"INCOMPLETE",
                         "nativeEvidenceComplete":False,
                         "reason":code if re.fullmatch(r"[A-Z0-9_]{1,80}",code) else type(ex).__name__})
    counts=collections.Counter(x["status"] for x in rows)
    partial=sum(not x["nativeEvidenceComplete"] for x in rows)
    status=("INCOMPLETE" if unexpected or partial or counts["READY"]+counts["BOOTSTRAP_FAILED"]!=len(expected)
            else "COMPLETE_WITH_BOOTSTRAP_FAILURES" if counts["BOOTSTRAP_FAILED"] else "COMPLETE_ALL_READY")
    return {"schema":"REL755_NATIVE_SUMMARY_V1","scope":"INVESTIGATION_ONLY",
            "sourceHeadSha":source,"workflowSha":workflow,"runId":run,"runAttempt":attempt,
            "samplesPerArm":samples,"expected":len(expected),"status":status,"counts":dict(counts),
            "partial":partial,"unexpectedArtifacts":unexpected,
            "sectorDispositions":dict(collections.Counter(x.get("sectorDisposition","UNAVAILABLE") for x in rows)),
            "timeoutStages":[{"arm":x["arm"],"iteration":x["iteration"],**t}
                             for x in rows for t in x.get("progress",{}).get("timeoutStages",[])],
            "rows":rows,"rootCause":"REQUIRES_INDEPENDENT_DISPOSITION","remediation":"NOT_APPLIED"}


def main():
    p=argparse.ArgumentParser()
    sub=p.add_subparsers(dest="command",required=True)
    a=sub.add_parser("plan")
    a.add_argument("--samples",type=int,required=True)
    a=sub.add_parser("summarize")
    a.add_argument("--samples",type=int,required=True)
    a.add_argument("--root",type=Path,required=True)
    a.add_argument("--output",type=Path,required=True)
    args=p.parse_args()
    if args.command=="plan":
        print(json.dumps({"include":legacy.plan(args.samples)},separators=(",",":")))
        return 0
    identity=tuple(os.environ[k] for k in ("REL75_SOURCE_SHA","GITHUB_SHA","GITHUB_RUN_ID","GITHUB_RUN_ATTEMPT"))
    result=summarize(args.root,args.samples,identity)
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(result,indent=2)+"\n",encoding="utf-8")
    print(result["status"],result["counts"])
    return 0 if result["status"]=="COMPLETE_ALL_READY" else 1


if __name__=="__main__":
    raise SystemExit(main())
