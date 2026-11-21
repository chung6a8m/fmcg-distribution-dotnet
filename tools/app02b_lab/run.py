"""Prepare, resume or validate a local lab bundle without executing a scenario."""

import argparse
from pathlib import Path
import sys

if __package__ in (None, ""):
    sys.dont_write_bytecode = True
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from tools.app02b_lab.bundle import append_attempt, prepare_run, selection_ids
from tools.app02b_lab.context import (
    approved_root, checkout_sha, collect_inputs, validate_outside_checkout, validate_run_id,
)
from tools.app02b_lab.contract import LabError, canonical_json, child, read_json, require, safe_path
from tools.app02b_lab.schema import validate_lab_bundle


class _Parser(argparse.ArgumentParser):
    def error(self, message):
        # argparse includes untrusted argument values in its default errors.
        raise LabError("INVALID_ARGUMENTS")


def exit_code(results, *, integrity_ok=True) -> int:
    statuses = tuple(results)
    if not integrity_ok or "FAIL" in statuses:
        return 1
    if statuses and all(status == "PASS" for status in statuses):
        return 0
    return 2


def _emit(value):
    print(canonical_json({"originalGateStatus": "NOT_VERIFIED", **value}).decode("ascii").strip())


def main(argv=None) -> int:
    parser = _Parser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--config", type=Path)
    parser.add_argument("--selection", default="all")
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--evidence-root", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, help="independently retained exact journal checkpoint")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--resume", action="store_true")
    mode.add_argument("--validate", action="store_true", help="validate an existing bundle without writing")
    try:
        args = parser.parse_args(argv)
        validate_run_id(args.run_id)
        selected = selection_ids(args.selection)
        evidence = approved_root(args.evidence_root)
        root = child(evidence, args.run_id, missing=True)
        checkpoint = None
        if args.checkpoint is not None:
            require(args.validate or args.resume, "CHECKPOINT_REQUIRES_EXISTING_RUN")
            pin = safe_path(args.checkpoint).resolve(strict=True)
            validate_outside_checkout(pin)
            require(not pin.is_relative_to(evidence), "CHECKPOINT_INSIDE_EVIDENCE_ROOT")
            checkpoint = read_json(pin)
            require(isinstance(checkpoint, dict), "INVALID_EXTERNAL_CHECKPOINT")
        if not args.validate:
            require(args.config is not None, "PUBLIC_CONFIGURATION_REQUIRED")
            if not args.resume:
                require(not root.exists(), "RUN_ALREADY_EXISTS")
            else:
                require(root.is_dir(), "RUN_TO_RESUME_MISSING")
            inputs = collect_inputs(args.config, evidence, checkout_sha(), args.run_id)
            if not args.resume:
                prepare_run(evidence, inputs)
            append_attempt(root, inputs, selected, checkpoint=checkpoint)
        report = validate_lab_bundle(root, checkpoint=checkpoint if args.validate else None)
        results = {key: report.coverage.get(key, "NOT_RUN") for key in selected}
        _emit({
            "validation": report.status, "selectedResults": results,
            "checkpoint": report.checkpoint,
            "attemptIds": [attempt["attemptId"] for attempt in report.attempts],
            "issues": [{"code": issue.code, "location": issue.location} for issue in report.issues],
        })
        return exit_code(results.values(), integrity_ok=not report.issues)
    except LabError as error:
        blocked = error.code in {
            "PUBLIC_CONFIGURATION_REQUIRED", "PATH_MISSING",
            "PROTECTED_HOST_OR_ADMISSION_MISSING", "TARGET_LINEAGE_NOT_ADMITTED",
        }
        _emit({"validation": "BLOCKED" if blocked else "FAIL",
               "issues": [{"code": error.code, "location": error.location}]})
        return 2 if blocked else 1
    except OSError:
        _emit({"validation": "FAIL", "issues": [{"code": "LAB_IO_FAILURE", "location": ""}]})
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
