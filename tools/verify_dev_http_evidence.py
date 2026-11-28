"""Fail-closed diagnostic matrix evidence check; never replaces business assertions or Full CI."""
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import xml.etree.ElementTree as ET

SHA = re.compile(r"[0-9a-f]{40}\Z")
RUN = re.compile(r"[0-9a-f]{32}\Z")
D2_SCOPES = ("initial", "D2-07-A", "D2-07-B", "D2-14-A", "D2-14-B",
             "D2-15-A", "D2-15-B", "A-to-B", "B-to-A")
PROFILES = {"F01": ("dev-f01", "sql-result.json", "app02c5", "visits.spec.ts", 1),
            "F06": ("dev-f06", "sql-result.json", "app02c5", "cost-adjustments.spec.ts", 1),
            "D2": ("app02d2", "sql-review.json", "app02d2", None, 12)}


def require(condition, code):
    if not condition:
        raise ValueError(code)


def read_owned(root, path, limit=2 * 1024 * 1024):
    # Fixed relative paths under the checkout; reject symlinks and Windows junctions.
    current = root
    for part in path.parts:
        require(part not in ("", ".", "..") and not Path(part).is_absolute(), "unsafePath")
        current = current / part
        info = current.lstat()
        require(not stat.S_ISLNK(info.st_mode) and
                not (getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)),
                "reparsePath")
    require(current.is_file() and 0 < current.stat().st_size <= limit, "invalidFile")
    data = current.read_bytes()
    require(0 < len(data) <= limit, "invalidSize")
    return data


def read_json(root, path):
    return json.loads(read_owned(root, path).decode("utf-8-sig"))


def digest(data):
    return hashlib.sha256(data).hexdigest()


def verify(root, expected):
    root = Path(root)
    scenario = expected["scenario"]
    require(scenario in PROFILES and expected["arm"] in ("baseline", "instrumented"), "invalidArm")
    require(SHA.fullmatch(expected["sourceHead"]) and SHA.fullmatch(expected["checkout"]), "invalidSource")
    require(expected["run"].isdigit() and expected["attempt"].isdigit() and expected["iteration"] in (1, 2),
            "invalidAttempt")
    comparison = read_json(root, Path("TestResults/dev-http-comparison.json"))
    require(comparison["schema"] == "DEV_HTTP_COMPARISON_V1" and
            all(comparison.get(k) == v for k, v in expected.items()) and
            comparison["scenarioOutcome"] == "success" and
            comparison["witnessEnabled"] is (expected["arm"] == "instrumented"), "comparisonIdentity")
    profile, sql_name, browser_profile, spec, tests = PROFILES[scenario]
    base = Path("TestResults") / profile
    # Only a single original scenario per matrix runner, with a nonce-bound SQL receipt.
    candidates = list((root / base).glob("*/" + sql_name))
    require(len(candidates) == 1, "sqlReceiptCount")
    run_id = candidates[0].parent.name
    require(RUN.fullmatch(run_id), "invalidFixtureId")
    folder = base / run_id
    sql_bytes = read_owned(root, folder / sql_name)
    sql = json.loads(sql_bytes.decode("utf-8-sig"))
    require(sql.get("runId", sql.get("RunId")) == run_id and
            sql.get("source", sql.get("Source")) == expected["checkout"], "sqlIdentity")
    if scenario == "F01":
        require(sql.get("Status") == "PASS", "f01BusinessReceipt")
    elif scenario == "F06":
        require(sql.get("matches") is True, "f06BusinessReceipt")
    else:
        require(len(sql.get("cases", [])) == 36 and all(c.get("status", c.get("Status")) == "PASS"
                for c in sql["cases"]), "d2BusinessReceipt")
    junit_path = Path("web") / ("test-results-" + browser_profile) / run_id / "junit.xml"
    report = read_owned(root, junit_path)
    # Playwright produces UTF-8 XML; disallow declarations capable of entity expansion.
    report_text = report.decode("utf-8-sig")
    require("<!DOCTYPE" not in report_text.upper() and "<!ENTITY" not in report_text.upper(), "unsafeXml")
    doc = ET.fromstring(report_text)
    require(doc.tag == "testsuites" and doc.get("tests") == str(tests) and
            all(doc.get(k) == "0" for k in ("failures", "errors", "skipped")), "junitTotals")
    cases = doc.findall("./testsuite/testcase")
    require(len(cases) == tests and all(not any(c.find(k) is not None for k in
            ("failure", "error", "skipped")) for c in cases), "junitCases")
    if spec:
        require(all(c.get("classname") == spec for c in cases), "junitSpec")
    report_hash = digest(report)
    if scenario in ("F06", "D2"):
        require(sql.get("browserReportSha256", "").lower() == report_hash, "junitDigest")
    scopes = D2_SCOPES if scenario == "D2" else ("initial",)
    observer_count = 0
    files = list((root / folder).glob("d2-peer-*-observer.json"))
    if expected["arm"] == "baseline":
        require(not files and not list((root / folder).glob("d2-peer-*-startup.json")), "baselineWitness")
    else:
        require(len(files) == 2 * len(scopes), "observerInventory")
        for scope in scopes:
            startup = read_json(root, folder / f"d2-peer-{scope}-startup.json")
            require(startup["schema"] == "D2_PEER_STARTUP_V1" and startup["source"] == expected["checkout"] and
                    startup["runId"] == run_id and startup["profile"] == profile and startup["direction"] == scope and
                    startup["tenantAStarted"] is True and startup["tenantBStarted"] is True, "observerStartup")
            for tenant in ("tenant-a", "tenant-b"):
                prefix = f"d2-peer-{scope}-{tenant}"
                observer = read_json(root, folder / (prefix + "-observer.json"))
                cleanup = read_json(root, folder / (prefix + "-cleanup.json"))
                for row in (observer, cleanup):
                    require(row["source"] == expected["checkout"] and row["runId"] == run_id and
                            row["profile"] == profile and row["scope"] == scope and row["tenant"] == tenant, "observerIdentity")
                proof = observer.get("startupProof") or {}
                require(observer["schema"] == "DEV_SQL_OBSERVER_V1" and observer["status"] == "ready" and
                        observer["readinessSource"] == "flushedSample" and proof.get("status") == "ready" and
                        observer["startupBudgetMs"] == 4000 and
                        0 <= observer["startupElapsedMs"] < 4000 and observer["startupFailure"] == "none",
                        "observerAdmission")
                require(cleanup["schema"] == "DEV_SQL_OBSERVER_CLEANUP_V1" and
                        cleanup["cleanup"] == "exited" and cleanup["exitCode"] == 0, "observerCleanup")
                rows = [json.loads(line) for line in read_owned(root, folder / (prefix + "-sql.ndjson")).splitlines()]
                require(len(rows) >= 4 and rows[0]["event"] == "start" and rows[0]["source"] == expected["checkout"] and
                        rows[0]["runId"] == run_id and rows[0]["profile"] == profile and rows[0]["scope"] == scope and
                        rows[0]["tenant"] == tenant and rows[0]["pid"] == observer["pid"] and
                        rows[1]["event"] == "available" and rows[2]["event"] == "sample" and
                        rows[2]["truncated"] is False and rows[2]["utc"] == proof["firstSampleUtc"] and
                        rows[-1]["event"] == "stop" and rows[-1]["reason"] == "parentEnded" and
                        rows[-1]["finalSampleStatus"] == "usable" and rows[-2]["finalSample"] is True,
                        "observerSamples")
                observer_count += 1
    return {"schema": "DEV_HTTP_EVIDENCE_VERIFICATION_V1", **expected, "fixtureRunId": run_id,
            "status": "BROWSER_AND_STARTUP_VERIFIED", "browserTests": tests,
            "browserReport": junit_path.as_posix(), "browserReportSha256": report_hash,
            "sqlReceipt": (folder / sql_name).as_posix(), "sqlReceiptSha256": digest(sql_bytes),
            "readyObservers": observer_count,
            "requestCorrelationAndSqlBrackets": "REQUIRES_INDEPENDENT_RAW_REVIEW"}


def main():
    expected = {"sourceHead": os.environ["SOURCE_HEAD"], "checkout": os.environ["GITHUB_SHA"],
                "run": os.environ["GITHUB_RUN_ID"], "attempt": os.environ["GITHUB_RUN_ATTEMPT"],
                "scenario": os.environ["SCENARIO"], "arm": os.environ["ARM"],
                "iteration": int(os.environ["ITERATION"])}
    output = Path("TestResults/dev-http-evidence-verification.json")
    try:
        receipt = verify(Path.cwd(), expected)
    except (OSError, ValueError, KeyError, TypeError, ET.ParseError) as error:
        # Only fixed codes or exception type; no raw request, payload or path in the receipt.
        code = str(error) if type(error) is ValueError and re.fullmatch(r"[a-zA-Z]+", str(error)) else type(error).__name__
        output.write_text(json.dumps({"schema": "DEV_HTTP_EVIDENCE_VERIFICATION_V1", **expected,
                                      "status": "DIAGNOSTIC_EVIDENCE_INCOMPLETE", "reason": code}), encoding="utf-8")
        raise SystemExit("Diagnostic evidence incomplete: " + code) from None
    output.write_text(json.dumps(receipt, indent=2), encoding="utf-8")
    print("Diagnostic browser and observer startup evidence verified; raw correlation review remains required.")


if __name__ == "__main__":
    main()
