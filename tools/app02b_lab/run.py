"""Prepare, resume or validate a local lab bundle without executing a scenario."""

import sys


_SOURCE_HEAD = None

if __name__ == "__main__":
    import os

    if not (sys.flags.isolated and sys.flags.no_site and sys.flags.dont_write_bytecode):
        # Keep the compatibility launcher on the interpreter's standard
        # library path. The child bypasses site initialization and PYTHONPATH.
        stdlib = os.path.dirname(os.__file__)
        sys.path[:] = [stdlib, os.path.join(stdlib, "lib-dynload"),
                       os.path.join(sys.base_exec_prefix, "DLLs")]
        import subprocess

        raise SystemExit(subprocess.call([sys.executable, "-I", "-S", "-B", *sys.argv]))

    from importlib.machinery import ModuleSpec, SourceFileLoader
    from importlib.util import spec_from_file_location
    from pathlib import Path
    import subprocess

    _REPO = Path(__file__).resolve().parents[2]
    _GIT_ENV = {key: value for key, value in os.environ.items()
                if not key.upper().startswith("GIT_")}

    def _git_source(*args):
        try:
            return subprocess.check_output(
                ["git", "--no-replace-objects", "-C", str(_REPO), *args],
                stderr=subprocess.DEVNULL, env=_GIT_ENV, timeout=30,
            )
        except (OSError, subprocess.SubprocessError):
            print('{"validation":"FAIL","originalGateStatus":"NOT_VERIFIED",'
                  '"issues":[{"code":"GIT_SOURCE_BOOTSTRAP_FAILED","location":""}]}')
            raise SystemExit(1) from None

    _SOURCE_HEAD = _git_source("rev-parse", "--verify", "HEAD^{commit}").decode("ascii").strip()

    class _LabSourceLoader(SourceFileLoader):
        def read_pinned_data(self, filename):
            relative = Path(filename).relative_to(_REPO).as_posix()
            return _git_source("cat-file", "blob", f"{_SOURCE_HEAD}:{relative}")

        def get_code(self, fullname):
            filename = self.get_filename(fullname)
            return self.source_to_code(self.read_pinned_data(filename), filename)

    class _LabSourceFinder:
        package = "tools.app02b_lab"
        modules = {"bundle", "catalog", "context", "contract", "schema"}
        directory = Path(__file__).resolve().parent

        def find_spec(self, fullname, path=None, target=None):
            if fullname == "tools":
                return ModuleSpec(fullname, loader=None, is_package=True)
            if fullname == self.package:
                source = self.directory / "__init__.py"
                return spec_from_file_location(
                    fullname, source, loader=_LabSourceLoader(fullname, str(source)),
                    submodule_search_locations=[],
                )
            if fullname.startswith(self.package + "."):
                module = fullname.removeprefix(self.package + ".")
                if module in self.modules:
                    source = self.directory / (module + ".py")
                    return spec_from_file_location(
                        fullname, source, loader=_LabSourceLoader(fullname, str(source)),
                    )
                raise ModuleNotFoundError(fullname)
            return None

    sys.meta_path.insert(0, _LabSourceFinder())

import argparse
from pathlib import Path

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
            inputs = collect_inputs(args.config, evidence, _SOURCE_HEAD or checkout_sha(), args.run_id)
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
