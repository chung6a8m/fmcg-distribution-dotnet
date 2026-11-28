"""REL-75.4: finite, exact-attempt hosted reproduction evidence (not a required gate)."""
import argparse
import json
import hashlib
from datetime import datetime
from decimal import Decimal
from uuid import UUID
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


def fixture_contract():
    path = Path(__file__).resolve().parents[1] / "tests/FmcgDms.Api.Tests/Fixtures/app02a4-expected.json"
    raw = path.read_bytes()
    lf = raw.replace(b"\r\n", b"\n")
    # Git may convert the fixture's line endings on Windows. Accept only the
    # two byte hashes of the same checked-out contract, not arbitrary values.
    hashes = {hashlib.sha256(x).hexdigest().upper() for x in
              (raw, lf, lf.replace(b"\n", b"\r\n"))}
    return json.loads(raw.decode("utf-8-sig")), hashes


def integer(value, minimum=0):
    return type(value) is int and value >= minimum


def number(value):
    require(type(value) in (int, float), "ORACLE_NONNUMERIC")
    n = Decimal(str(value))
    require(n.is_finite(), "ORACLE_NONFINITE")
    return n


def utc_ms(value):
    require(isinstance(value, str), "INVALID_UTC")
    date = datetime.fromisoformat(value.replace("Z", "+00:00"))
    require(date.tzinfo is not None, "INVALID_UTC")
    return int(date.timestamp() * 1000)


def diagnostic(root, ref, sample, identity, stage, pid, *, cell=True):
    require(isinstance(ref, str) and re.fullmatch(r"LAB-E02/[a-zA-Z0-9_-]{1,180}\.json", ref),
            "INVALID_DIAGNOSTIC_REFERENCE")
    found = list(root.rglob(Path(ref).name))
    require(len(found) == 1 and found[0].parent.name == "LAB-E02", "DIAGNOSTIC_FILE_SCOPE")
    d = one(root, Path(ref).name)
    source, workflow, run, attempt = identity
    ctx = d["context"]
    require(ctx["sourceHeadSha"] == source and ctx["workflowSha"] == workflow
            and ctx["testRunId"] == f"app02a4-{run}-{attempt}"
            and ctx["tenantId"] == sample["Tenant"] and ctx["group"] == "A11-09"
            and ctx["previousPid"] == sample["OldPid"], "DIAGNOSTIC_SOURCE")
    if cell:
        require((ctx["fixtureNonce"], ctx["arm"], ctx["iteration"]) ==
                (sample["Nonce"], sample["Arm"], sample["Iteration"]), "DIAGNOSTIC_CELL")
    p = d["process"]
    require(d["stage"] == stage and p["pid"] == pid and type(p["workerEnabled"]) is bool
            and type(p["exited"]) is bool and integer(p["port"], 1) and p["port"] <= 65535
            and utc_ms(p["startedUtc"]) <= utc_ms(d["capturedUtc"]), "DIAGNOSTIC_PROCESS")
    for stream in ("stdout", "stderr"):
        log = d[stream]
        require(integer(log["retainedChars"]) and integer(log["discardedChars"])
                and type(log["truncated"]) is bool and type(log["complete"]) is bool
                and log["captureError"] is None and type(log["recoveryObservationsTruncated"]) is bool
                and isinstance(log["observations"], list)
                and isinstance(log["recoveryObservations"], list), "DIAGNOSTIC_STREAM")
    return d


def sql_numbers(node):
    if isinstance(node, dict):
        for k, v in node.items():
            if k == "sqlErrorNumber" and type(v) is int:
                yield v
            yield from sql_numbers(v)
    elif isinstance(node, list):
        for v in node:
            yield from sql_numbers(v)


def validate_oracle(o, sample, identity, passed):
    source, workflow, run, attempt = identity
    tenant, nonce = sample["Tenant"], sample["Nonce"]
    peer, slot = ("B", "A") if tenant == "tenant-a" else ("A", "B")
    fixture, hashes = fixture_contract()
    require(o["Schema"] == "APP02A4_SQL_ORACLE_V1" and o["Group"] == "A11-09"
            and o["TenantId"] == tenant and o["SourceHeadSha"] == source
            and o["WorkflowSha"] == workflow and o["TestRunId"] == f"app02a4-{run}-{attempt}",
            "ORACLE_SOURCE")
    require(o["PeerDatabase"] == f"Fmcg_App00_{nonce}_{peer}", "PEER_NONCE")
    require(o["OracleSha256"].upper() in hashes, "ORACLE_FIXTURE_HASH")
    require(o["Status"] == sample["Status"], "ORACLE_STATUS")
    peer_ok = (bool(HEX64.fullmatch(o["PeerBeforeSha256"].lower()))
               and o["PeerBeforeSha256"] == o["PeerAfterSha256"])
    require(sample["PeerUnchanged"] is peer_ok, "PEER_RECEIPT")
    checkpoints = o["Checkpoints"]
    phases = list(fixture["groups"]["A11-09"])
    require(isinstance(checkpoints, list) and len(checkpoints) <= len(phases)
            and [c["Phase"] for c in checkpoints] == phases[:len(checkpoints)], "ORACLE_PHASES")
    checked, matched, trip = 0, 0, None
    for c in checkpoints:
        if trip is None:
            trip = str(UUID(c["TripId"]))
        require(str(UUID(c["TripId"])) == trip and c["Database"] == f"Fmcg_App00_{nonce}_{slot}"
                and bool(HEX64.fullmatch(c["SqlSha256"].lower()))
                and integer(c["SqlTables"], 25) and integer(c["SqlRows"], 1)
                and isinstance(c["SqlStatusHistory"], list) and bool(c["SqlStatusHistory"]),
                "ORACLE_CHECKPOINT")
        expected = fixture["states"][fixture["groups"]["A11-09"][c["Phase"]]]
        require(len(c["Cells"]) == len(expected) == 24, "ORACLE_CELL_COUNT")
        for metric, exp, cell in zip(fixture["columns"], expected, c["Cells"]):
            actual = number(cell["Actual"])
            delta = actual - number(exp)
            match = delta == 0
            require(cell["Metric"] == metric and number(cell["Expected"]) == number(exp)
                    and number(cell["Difference"]) == delta and cell["Match"] is match,
                    "ORACLE_CELL_VALUE")
            checked += 1
            matched += int(match)
    require((sample["OracleChecked"], sample["OracleMatched"]) == (checked, matched)
            and (o["CheckedCells"], o["MatchedCells"]) == (checked, matched), "ORACLE_COUNT")
    if passed:
        require(o["Database"] == f"Fmcg_App00_{nonce}_{slot}" and peer_ok
                and checked == matched == 48 and len(checkpoints) == 2, "PASS_ORACLE")
    return fixture, trip


def bootstrap(root, identity):
    found = list(root.rglob("bootstrap-*.json"))
    require(len(found) == 1 and found[0].parent.name == "LocalDbBootstrap", "BOOTSTRAP_RECEIPT")
    d = one(root, found[0].name)
    require((d["sourceHeadSha"], d["workflowSha"], d["runId"], d["runAttempt"]) == identity
            and d["job"] == "sample" and d["schemaVersion"] == "CI_LOCALDB_BOOTSTRAP_V1",
            "BOOTSTRAP_SOURCE")
    require(d["status"] == "READY" and d["mode"] == "COLD" and d["instance"] == "MSSQLLocalDB"
            and integer(d["readinessAttempts"], 1) and d["readinessAttempts"] <= 3
            and d["failure"] is None and d["deadlineSeconds"] == 60, "BOOTSTRAP_NOT_COLD_READY")
    return d


def transactions(boundary):
    return {s["Spid"] for s in boundary["Sessions"] if s["HasUserTransaction"] is True}


def validate_boundaries(s):
    arm = s["Arm"]
    boundaries = s["Boundaries"]
    require(isinstance(boundaries, list) and len(boundaries) <= 12, "BOUNDARY_COUNT")
    points = {}
    previous = 0
    for b in boundaries:
        key = (b["Phase"], b["Point"])
        require(key not in points and integer(b["UtcMs"], 1) and b["UtcMs"] >= previous,
                "BOUNDARY_TIME_ORDER")
        previous = b["UtcMs"]
        points[key] = b
        sessions = b["Sessions"]
        ids = {x["Spid"] for x in sessions}
        require(len(ids) == len(sessions) and all(
            integer(x["Spid"], 1) and integer(x["BlockingSpid"])
            and (x["BlockingSpid"] == 0 or x["BlockingSpid"] in ids - {x["Spid"]})
            and type(x["HasUserTransaction"]) is bool for x in sessions), "BOUNDARY_SESSION")
        require(b["Point"] != "released" or not sessions, "RELEASE_HAS_SESSIONS")
    def at(phase, point):
        require((phase, point) in points, "CONTROLLED_BOUNDARY_NOT_REACHED")
        return points[(phase, point)]
    def progress(phase):
        alternatives = [points[(phase, p)] for p in ("reached", "sql-wait") if (phase, p) in points]
        require(len(alternatives) == 1, "CONTROLLED_PROGRESS_NOT_OBSERVED")
        return alternatives[0]
    def sequence(*items):
        require([points_key for points_key in points] == [(x["Phase"], x["Point"]) for x in items],
                "BOUNDARY_SEQUENCE")
    def pair(first, second):
        a, both = transactions(first), transactions(second)
        require(len(a) == 1 and len(both) == 2 and a < both and s["OverlapObserved"] is True,
                "TRANSACTION_OVERLAP_NOT_OBSERVED")
        return next(iter(a)), next(iter(both - a))
    def released_before_ack(*items):
        if s["IntakeAckUtcMs"] is not None:
            require(all(x["UtcMs"] <= s["IntakeAckUtcMs"] for x in items), "RELEASE_AFTER_ACK")
    if arm == "worker-off":
        require(not boundaries and s["OverlapObserved"] is False, "WORKER_OFF_BOUNDARIES")
    elif arm in ("before-sweep", "reverse-backlog"):
        first, release = at("SweepBeforeSelect", "reached"), at("SweepBeforeSelect", "released")
        sequence(first, release)
        require(integer(s["IntakeAckUtcMs"], 1)
                and first["UtcMs"] <= s["IntakeAckUtcMs"] <= release["UtcMs"]
                and s["OverlapObserved"] is False, "SWEEP_RELEASE_BEFORE_ACK")
    elif arm == "c-p-overlap":
        first, role = at("CBeforePrerequisite", "reached"), at("PAfterRole", "reached")
        c, p = pair(first, role)
        role_release = at("PAfterRole", "released")
        before = progress("PBeforeCommit")
        cr, pr = at("CBeforePrerequisite", "released"), at("PBeforeCommit", "released")
        sequence(first, role, role_release, before, cr, pr)
        require(transactions(before) == {c, p}, "P_PROGRESS_PAIR")
        if before["Point"] == "sql-wait":
            require(any(x["Spid"] == p and x["BlockingSpid"] == c for x in before["Sessions"]),
                    "P_WAIT_DIRECTION")
        released_before_ack(cr, pr)
    elif arm == "p-commit-sweep":
        first, p_hold = at("SweepBeforeSelect", "reached"), at("PBeforeCommit", "reached")
        pids = transactions(p_hold)
        require(len(pids) == 1, "P_COMMIT_TRANSACTION")
        p = next(iter(pids))
        sr, sweep = at("SweepBeforeSelect", "released"), progress("SweepInFlight")
        pr, cr = at("PBeforeCommit", "released"), at("CBeforePrerequisite", "released")
        sequence(first, p_hold, sr, sweep, pr, cr)
        active = transactions(sweep)
        require(p in active and (len(active) == 2 if sweep["Point"] == "reached" else
                any(x["Spid"] != p and x["BlockingSpid"] == p for x in sweep["Sessions"])),
                "SWEEP_WAIT_DIRECTION")
        released_before_ack(pr)
    elif arm == "outbox-overlap":
        first, role = at("OutboxAfterClaimRead", "reached"), at("PAfterRole", "reached")
        pair(first, role)
        first_r, role_r = at("OutboxAfterClaimRead", "released"), at("PAfterRole", "released")
        sequence(first, role, first_r, role_r)
        released_before_ack(first_r, role_r)


def graph_cycles(s, start_ms, capture_ms):
    x = s["XeObservation"]
    require(s["GraphCapture"] == "CAPTURED" and s["GraphError"] is None
            and isinstance(x, dict) and x["Complete"] is True and x["Truncated"] is False,
            "XE_INCOMPLETE")
    require(all(integer(x[k]) and x[k] == 0 for k in ("TargetDroppedCount", "SessionDroppedEvents",
                "SessionDroppedBuffers", "LargestDroppedEventSize", "UnprojectedReports")), "XE_DROPPED_OR_UNSCOPED")
    require(integer(x["DatabaseId"], 1) and integer(x["EventCount"])
            and integer(x["ParsedEventCount"]) and x["TotalEventsProcessed"] == x["EventCount"]
            == x["ParsedEventCount"] == len(x["Graphs"]), "XE_EVENT_COUNT")
    cycles = 0
    modes = {"S", "U", "X", "IS", "IU", "IX", "SIX", "SIU", "UIX", "Sch-S", "Sch-M",
             "RangeS-S", "RangeS-U", "RangeI-N", "RangeI-S", "RangeI-U", "RangeI-X",
             "RangeX-S", "RangeX-U", "RangeX-X", "BU", "NL", "OTHER"}
    kinds = {"keylock", "pagelock", "objectlock", "ridlock", "applicationlock",
             "exchangeEvent", "metadatalock", "rowgrouplock", "hobtlock", "OTHER"}
    tables = {"App02EventReceipt", "App02EventProgress", "App02ActorRoleGrant",
              "App02AcceptanceApplication", "App02OutboxMessage", "App02OutboxDelivery",
              "App02SyntheticConsumerInbox", "CashHandoverOffer", "BusinessSource",
              "BusinessCommand", "CashierCountEvidence", "Trip", "LedgerEffect", "OTHER"}
    for g in x["Graphs"]:
        require(g["HostPid"] in (s["IntakePid"], s["RecoveryPid"])
                and g["DatabaseId"] == x["DatabaseId"] and integer(g["EventUtcMs"], 1)
                and start_ms <= g["EventUtcMs"] <= capture_ms, "GRAPH_SCOPE")
        spids = set(g["Spids"])
        victims = set(g["VictimSpids"])
        require(2 <= len(spids) == len(g["Spids"]) <= 32
                and all(integer(p, 1) for p in spids) and victims and victims <= spids
                and len(victims) == len(g["VictimSpids"]) and bool(g["Resources"]), "GRAPH_PARTICIPANTS")
        edges = set()
        for resource in g["Resources"]:
            require(resource["Kind"] in kinds and resource["Table"] in tables
                    and (resource["HobtId"] is None or integer(resource["HobtId"], 1))
                    and bool(resource["Edges"]), "GRAPH_RESOURCE")
            for edge in resource["Edges"]:
                owner, waiter = edge["OwnerSpid"], edge["WaiterSpid"]
                require(owner in spids and waiter in spids and edge["OwnerMode"] in modes
                        and edge["WaiterMode"] in modes, "GRAPH_EDGE")
                if owner != waiter:
                    edges.add((waiter, owner))
        def reaches(start, goal, seen):
            if start == goal:
                return True
            if start in seen:
                return False
            return any(reaches(n, goal, seen | {start}) for a, n in edges if a == start)
        require(g["Cycle"] is True and any(reaches(owner, waiter, set()) for waiter, owner in edges),
                "GRAPH_CYCLE_NOT_PROVEN")
        cycles += 1
    return cycles


def validate(root, key, identity):
    arm, tenant, iteration = key
    s = one(root, "sample.json")
    e = one(root, "execution.json")
    o = one(root, tenant + "-A11-09-oracle.json")
    require(s["Schema"] == "REL75_A11_09_V2", "SAMPLE_SCHEMA")
    require((s["Arm"], s["Tenant"], s["Iteration"]) == key, "CELL_ID")
    require((s["SourceSha"], s["WorkflowSha"], s["Run"], s["Attempt"]) == identity, "SAMPLE_SOURCE")
    require(bool(HEX32.fullmatch(s["Nonce"])), "NONCE")
    require(e["schema"] == "REL75_A11_EXECUTION_V1", "EXEC_SCHEMA")
    require((e["arm"], e["tenant"], e["iteration"]) == key, "EXEC_CELL")
    require((e["source"], e["workflow"], e["run"], e["attempt"]) == identity, "EXEC_SOURCE")
    require(e["trxCount"] == 1 and e["testCount"] == 1 and e["correctMethod"] is True, "EXACT_TEST_EXECUTION")
    require(s["Status"] in ("PASS", "FAIL"), "SAMPLE_STATUS")
    passed = s["Status"] == "PASS"
    require((e["outcome"] == "Passed" and e["exitCode"] == 0) if passed
            else (e["outcome"] == "Failed" and e["exitCode"] != 0), "EXEC_STATUS")
    fixture, trip = validate_oracle(o, s, identity, passed)
    boot = bootstrap(root, identity)
    require(s["DiagnosticError"] is None, "DIAGNOSTIC_ERROR")
    require(integer(s["OldPid"], 1) and integer(s["IntakePid"], 1)
            and s["OldPid"] != s["IntakePid"], "NEW_PID_NOT_OBSERVED")
    previous = s["PreviousProcess"]
    require(previous["Pid"] == s["OldPid"] and previous["WorkerEnabled"] is False
            and previous["Exited"] is True and type(previous["ExitCode"]) is int
            and utc_ms(boot["completedUtc"]) <= utc_ms(previous["StartedUtc"])
            <= utc_ms(previous["StoppedUtc"]), "PREVIOUS_PROCESS")
    active_pid = s["RecoveryPid"] if s["RecoveryPid"] is not None else s["IntakePid"]
    obs = diagnostic(root, s["ProcessObservation"], s, identity, "rel75-observation", active_pid)
    start_ms = utc_ms(previous["StoppedUtc"])
    capture_ms = utc_ms(obs["capturedUtc"])
    require(start_ms <= utc_ms(obs["process"]["startedUtc"]) <= capture_ms, "PROCESS_TIME_SCOPE")
    cycles = graph_cycles(s, start_ms, capture_ms)
    validate_boundaries(s)
    require(all(start_ms <= x["UtcMs"] <= capture_ms for x in s["Boundaries"]), "BOUNDARY_PROCESS_TIME")
    require(s["BacklogCount"] == {"outbox-overlap": 1, "reverse-backlog": 3}.get(arm, 0), "BACKLOG_COUNT")
    require(s["HttpDiagnostic"] == o["FailureDiagnostic"] and s["RecoveryDiagnostic"] == o["RecoveryDiagnostic"],
            "ORACLE_DIAGNOSTIC_REFERENCE")
    if passed:
        require(s["HttpStatus"] == 202 and s["Ack"] == "RECEIVED_COMMITTED"
                and s["Stage"] == "oracle-verified" and integer(s["RecoveryPolls"], 1)
                and s["HttpDiagnostic"] is None and s["RecoveryDiagnostic"] is None
                and s["HttpLogSqlNumbers"] == [] and obs["http"] is None and obs["recovery"] is None,
                "PASS_CONTRACT")
        require(integer(s["RecoveryPid"], 1) and s["RecoveryPid"] != s["OldPid"]
                and obs["process"]["workerEnabled"] is True and obs["process"]["exited"] is False,
                "RECOVERY_PID")
        require((s["RecoveryPid"] != s["IntakePid"]) if arm == "worker-off"
                else (s["RecoveryPid"] == s["IntakePid"]), "WORKER_CONTROL")
        require(start_ms <= s["IntakeAckUtcMs"] <= s["RecoveryUtcMs"] <= capture_ms
                and s["RecoveryUtcMs"] - s["IntakeAckUtcMs"] <= 30000, "RECOVERY_DEADLINE")
        if arm == "worker-off":
            intake = diagnostic(root, s["IntakeObservation"], s, identity,
                                "rel75-worker-off-intake", s["IntakePid"])
            require(intake["process"]["workerEnabled"] is False and intake["process"]["exited"] is False
                    and all(not intake[k]["recoveryObservations"] for k in ("stdout", "stderr"))
                    and start_ms <= utc_ms(intake["process"]["startedUtc"]) <= s["IntakeAckUtcMs"]
                    <= utc_ms(intake["capturedUtc"]) <= utc_ms(obs["process"]["startedUtc"])
                    and [number(v) for v in s["WorkerOffFacts"]] ==
                    [number(v) for v in fixture["states"]["ready"]], "WORKER_OFF_CONTROL")
        else:
            require(s["IntakeObservation"] is None and s["WorkerOffFacts"] is None, "UNEXPECTED_WORKER_OFF_CONTROL")
        require(not any(obs[k]["recoveryObservationsTruncated"] for k in ("stdout", "stderr")),
                "SWEEP_TELEMETRY_TRUNCATED")
        sweeps = [v for v in obs["stdout"]["recoveryObservations"] + obs["stderr"]["recoveryObservations"]
                  if v["kind"] == "sweep" and v["phase"] == "processing" and v["point"] == "start"]
        sweeps.sort(key=lambda x: x["utcMs"])
        require(bool(sweeps) and sweeps[0]["tenant"] == ("tenant-b" if arm == "reverse-backlog" else "tenant-a")
                and any(v["tenant"] == tenant for v in sweeps), "SWEEP_ORDER_NOT_OBSERVED")
        return s["Nonce"], "PASS_WITH_STRUCTURAL_WITNESS" if cycles else "PASS", cycles
    # Negative samples are not successful controls. Validate the actual
    # referenced scoped HTTP and recovery evidence, not just a claimed code.
    require(s["RecoveryDiagnostic"] is not None, "MISSING_RECOVERY_DIAGNOSTIC")
    recovery = diagnostic(root, s["RecoveryDiagnostic"], s, identity, s["Stage"], active_pid, cell=False)
    payload = recovery["recovery"]
    require(payload["schema"] == "REL75_RECOVERY_V1" and payload["fixtureNonce"] == s["Nonce"]
            and trip is not None and str(UUID(payload["tripId"])) == trip
            and payload["previousProcess"]["pid"] == s["OldPid"] and payload["previousProcess"]["exited"] is True
            and payload["sql"]["status"] == "CAPTURED" and isinstance(payload["sql"]["state"], dict)
            and payload["sql"]["error"] is None, "RECOVERY_SQL_EVIDENCE")
    if s["HttpStatus"] is not None and s["HttpStatus"] != 202:
        http = diagnostic(root, s["HttpDiagnostic"], s, identity, "post-restart-P", s["IntakePid"])
        require(http["http"]["status"] == s["HttpStatus"] and http["http"]["method"] == "POST"
                and http["http"]["path"] == "/api/synthetic/app02/events/intake", "HTTP_DIAGNOSTIC_STATUS")
        observed = sorted(set(sql_numbers(http)))
        require(s["HttpLogSqlNumbers"] == observed, "HTTP_SQL_NUMBERS")
        kind = "HTTP_500_WITH_1205_LOG" if s["HttpStatus"] == 500 and 1205 in observed else "HTTP_FAILURE"
    elif s["HttpStatus"] == 202 and s["Stage"] in ("recovery-poll", "recovery-deadline"):
        kind = "RECOVERY_FAILURE"
    else:
        kind = "OTHER_INVARIANT_FAILURE"
    return s["Nonce"], kind, cycles

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
        except (ValueError, TypeError, KeyError, OSError, AttributeError, ArithmeticError, IndexError, RecursionError) as exc:
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
    return {"schema": "REL75_A11_SUMMARY_V2", "source": source, "workflow": workflow,
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
