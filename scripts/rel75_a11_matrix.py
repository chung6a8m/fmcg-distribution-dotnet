"""REL-75.4: finite, exact-attempt hosted reproduction evidence (not a required gate)."""
import argparse
import json
import os
from pathlib import Path
import re
from collections import Counter

ARMS = ("worker-off", "before-sweep", "c-p-overlap", "p-commit-sweep",
        "outbox-overlap", "reverse-backlog")
TENANTS = ("tenant-a", "tenant-b")
HEX40 = re.compile(r"[0-9a-f]{40}")
HEX32 = re.compile(r"[0-9a-f]{32}")
HEX64 = re.compile(r"[0-9a-f]{64}")


def cells(samples):
    if samples not in (1, 5):
        raise ValueError("samples must be 1 or 5")
    return [(arm, tenant, i) for arm in ARMS for tenant in TENANTS
            for i in range(1, samples + 1)]


def plan(samples):
    return {"include": [{"arm": arm, "tenant": tenant, "iteration": i}
                        for arm, tenant, i in cells(samples)]}


def require(condition, code):
    if not condition:
        raise ValueError(code)


def one(root, name):
    found = list(root.rglob(name))
    require(len(found) == 1, "MISSING_OR_DUPLICATE_" + re.sub(r"[^A-Z0-9]+", "_", name.upper()))
    require(found[0].stat().st_size <= 2_000_000, "RECEIPT_TOO_LARGE")
    return json.loads(found[0].read_text(encoding="utf-8-sig"))


def diagnostic(root, ref):
    require(isinstance(ref, str) and re.fullmatch(r"LAB-E02/[a-zA-Z0-9_-]{1,180}\.json", ref),
            "INVALID_DIAGNOSTIC_REFERENCE")
    return one(root, Path(ref).name)


def spids(boundary):
    return {s["Spid"] for s in boundary["Sessions"] if s.get("HasUserTransaction", True)}


def validate(root, key, identity):
    arm, tenant, iteration = key
    source, workflow, run, attempt = identity
    s = one(root, "sample.json")
    e = one(root, "execution.json")
    o = one(root, tenant + "-A11-09-oracle.json")
    require(s["Schema"] == "REL75_A11_09_V1", "SAMPLE_SCHEMA")
    require((s["Arm"], s["Tenant"], s["Iteration"]) == key, "CELL_ID")
    require((s["SourceSha"], s["WorkflowSha"], s["Run"], s["Attempt"]) == identity, "SAMPLE_SOURCE")
    require(bool(HEX32.fullmatch(s["Nonce"])), "NONCE")
    require(e["schema"] == "REL75_A11_EXECUTION_V1", "EXEC_SCHEMA")
    require((e["arm"], e["tenant"], e["iteration"]) == key, "EXEC_CELL")
    require((e["source"], e["workflow"], e["run"], e["attempt"]) == identity, "EXEC_SOURCE")
    require(e["trxCount"] == 1 and e["testCount"] == 1 and e["correctMethod"] is True,
            "EXACT_TEST_EXECUTION")
    require(s["Status"] in ("PASS", "FAIL"), "SAMPLE_STATUS")
    passed = s["Status"] == "PASS"
    require((e["outcome"] == "Passed" and e["exitCode"] == 0) if passed
            else (e["outcome"] == "Failed" and e["exitCode"] != 0), "EXEC_STATUS")
    require(o["Schema"] == "APP02A4_SQL_ORACLE_V1" and o["Group"] == "A11-09"
            and o["TenantId"] == tenant and o["SourceHeadSha"] == source
            and o["WorkflowSha"] == workflow and o["TestRunId"] == f"app02a4-{run}-{attempt}",
            "ORACLE_SOURCE")
    peer = "B" if tenant == "tenant-a" else "A"
    require(o["PeerDatabase"] == f"Fmcg_App00_{s['Nonce']}_{peer}", "PEER_NONCE")
    require(s["OracleChecked"] == o["CheckedCells"] and s["OracleMatched"] == o["MatchedCells"],
            "ORACLE_COUNT")
    peer_ok = bool(HEX64.fullmatch(o["PeerBeforeSha256"].lower())) and (
        o["PeerBeforeSha256"] == o["PeerAfterSha256"])
    require(s["PeerUnchanged"] == peer_ok, "PEER_RECEIPT")
    require(s["GraphCapture"] == "CAPTURED", "XE_UNAVAILABLE_OR_CLEANUP_FAILED")
    require(s["DiagnosticError"] is None, "DIAGNOSTIC_ERROR")
    require(type(s["OldPid"]) is int and type(s["IntakePid"]) is int
            and s["OldPid"] > 0 and s["IntakePid"] > 0 and s["OldPid"] != s["IntakePid"],
            "NEW_PID_NOT_OBSERVED")
    diagnostic(root, s["ProcessObservation"])
    graphs = s["Graphs"]
    require(isinstance(graphs, list), "GRAPH_TYPE")
    graph_cycles = 0
    for g in graphs:
        require(g["HostPid"] in (s["IntakePid"], s["RecoveryPid"])
                and type(g["DatabaseId"]) is int and g["DatabaseId"] > 0, "GRAPH_SCOPE")
        require(len(set(g["Spids"])) >= 2 and set(g["VictimSpids"]) <= set(g["Spids"])
                and len(g["VictimSpids"]) >= 1 and len(g["Resources"]) >= 1, "GRAPH_PARTICIPANTS")
        for resource in g["Resources"]:
            require(resource["Edges"] and all(
                edge["OwnerSpid"] in g["Spids"] and edge["WaiterSpid"] in g["Spids"]
                for edge in resource["Edges"]), "GRAPH_EDGE")
        graph_cycles += int(g["Cycle"] is True)
    reached = {x["Phase"]: x for x in s["Boundaries"] if x["Point"] == "reached"}
    expected = {
        "worker-off": set(), "before-sweep": {"SweepBeforeSelect"},
        "c-p-overlap": {"CBeforePrerequisite", "PAfterRole"},
        "p-commit-sweep": {"SweepBeforeSelect", "PBeforeCommit"},
        "outbox-overlap": {"OutboxAfterClaimRead", "PAfterRole"},
        "reverse-backlog": {"SweepBeforeSelect"},
    }[arm]
    require(expected <= reached.keys(), "CONTROLLED_BOUNDARY_NOT_REACHED")
    if arm in ("c-p-overlap", "outbox-overlap"):
        require(s["OverlapObserved"] is True and len(spids(reached["PAfterRole"])) >= 2,
                "TRANSACTION_OVERLAP_NOT_OBSERVED")
    if arm == "p-commit-sweep":
        require(any(x["Phase"] == "SweepInFlight" for x in s["Boundaries"]),
                "SWEEP_OVERLAP_NOT_OBSERVED")
    require(s["BacklogCount"] == {"outbox-overlap": 1, "reverse-backlog": 3}.get(arm, 0),
            "BACKLOG_COUNT")
    if passed:
        slot = "A" if tenant == "tenant-a" else "B"
        require(o["Status"] == "PASS" and o["Database"] == f"Fmcg_App00_{s['Nonce']}_{slot}"
                and peer_ok and s["OracleChecked"] == s["OracleMatched"] == 48
                and [x["Phase"] for x in o["Checkpoints"]] == ["os-pending", "os-recovered"],
                "PASS_ORACLE")
        require(s["HttpStatus"] == 202 and s["Ack"] == "RECEIVED_COMMITTED"
                and s["Stage"] == "oracle-verified" and s["RecoveryPolls"] > 0, "PASS_CONTRACT")
        require(type(s["RecoveryPid"]) is int and s["RecoveryPid"] > 0
                and s["RecoveryPid"] != s["OldPid"], "RECOVERY_PID")
        require((s["RecoveryPid"] != s["IntakePid"]) if arm == "worker-off"
                else (s["RecoveryPid"] == s["IntakePid"]), "WORKER_CONTROL")
        require(0 <= s["RecoveryUtcMs"] - s["IntakeAckUtcMs"] <= 30000, "RECOVERY_DEADLINE")
        return s["Nonce"], "PASS_WITH_STRUCTURAL_WITNESS" if graph_cycles else "PASS", graph_cycles
    # Retain negative observations, not a successful sample or a waived gate.
    require(s["RecoveryDiagnostic"] is not None, "MISSING_RECOVERY_DIAGNOSTIC")
    diagnostic(root, s["RecoveryDiagnostic"])
    if s["HttpStatus"] is not None and s["HttpStatus"] != 202:
        diagnostic(root, s["HttpDiagnostic"])
        kind = ("HTTP_500_WITH_1205_LOG" if s["HttpStatus"] == 500
                and 1205 in s["HttpLogSqlNumbers"] else "HTTP_FAILURE")
    elif s["HttpStatus"] == 202 and s["Stage"] in ("recovery-poll", "recovery-deadline"):
        kind = "RECOVERY_FAILURE"
    else:
        kind = "OTHER_INVARIANT_FAILURE"
    return s["Nonce"], kind, graph_cycles


def summarize(samples, root, identity):
    source, workflow, run, attempt = identity
    require(bool(HEX40.fullmatch(source)) and bool(HEX40.fullmatch(workflow))
            and run.isdecimal() and attempt.isdecimal() and int(run) > 0 and int(attempt) > 0, "INVALID_CONTEXT")
    expected = cells(samples)
    prefix = f"rel75-a11-{run}-{attempt}-"
    names = {prefix + f"{a}-{t}-{i}": (a, t, i) for a, t, i in expected}
    unexpected = sum(1 for p in root.iterdir() if p.name not in names) if root.is_dir() else 0
    rows = []
    nonces = set()
    for name, key in names.items():
        row = dict(zip(("arm", "tenant", "iteration"), key))
        try:
            nonce, disposition, cycles = validate(root / name, key, identity)
            require(nonce not in nonces, "REUSED_NONCE")
            nonces.add(nonce)
            row.update(disposition=disposition, structural_cycles=cycles)
        except (ValueError, TypeError, KeyError, OSError, AttributeError) as exc:
            # Only our fixed uppercase classification is allowed; do not emit
            # exception messages containing raw JSON, filesystem or SQL values.
            label = str(exc)
            row.update(disposition="INCOMPLETE",
                       reason=label if re.fullmatch(r"[A-Z_]{1,80}", label) else type(exc).__name__)
        rows.append(row)
    counts = Counter(row["disposition"] for row in rows)
    status = ("INCOMPLETE" if unexpected or counts["INCOMPLETE"] else
              "COMPLETE_NO_REPRODUCTION" if counts["PASS"] == len(expected) else
              "COMPLETE_WITH_STRUCTURAL_WITNESSES" if counts["PASS"] + counts["PASS_WITH_STRUCTURAL_WITNESS"] == len(expected) else
              "COMPLETE_WITH_FAILURES")
    return {"schema": "REL75_A11_SUMMARY_V1", "source": source, "workflow": workflow,
            "run": run, "attempt": attempt, "samples_per_arm_tenant": samples,
            "expected": len(expected), "unexpected_artifacts": unexpected,
            "status": status, "counts": dict(counts), "rows": rows,
            "root_cause": "NOT_ESTABLISHED_BY_AGGREGATION"}


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("plan")
    p.add_argument("--samples", type=int, required=True)
    p = sub.add_parser("summarize")
    p.add_argument("--samples", type=int, required=True)
    p.add_argument("--root", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "plan":
        print(json.dumps(plan(args.samples), separators=(",", ":")))
        return 0
    identity = tuple(os.environ[k] for k in
                     ("REL75_SOURCE_SHA", "GITHUB_SHA", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT"))
    result = summarize(args.samples, args.root, identity)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(result["status"], result["counts"])
    return 0 if result["status"] == "COMPLETE_NO_REPRODUCTION" else 1


if __name__ == "__main__":
    raise SystemExit(main())
