"""Disposable source/target identities for tests only, with no operational claims."""

import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from tools.app02b_lab import context
from tools.app02b_lab.bundle import append_attempt, prepare_run, selection_ids
from tools.app02b_lab.catalog import FIXTURE, REPO
from tools.app02b_lab.contract import canonical_json, digest

RUN_ID = "b-lab-20261121T123456Z-6e018ed4"


def put(path: Path, value) -> None:
    path.write_bytes(canonical_json(value))


def get(path: Path):
    return json.loads(path.read_bytes())


class DisposableLab(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="lab test only ")
        self.addCleanup(temporary.cleanup)
        parent = Path(temporary.name).resolve()
        self.checkout = parent / "synthetic-source"
        self.checkout.mkdir()
        self.root = parent / "synthetic evidence"
        self.root.mkdir()
        self.bundle = self.root / RUN_ID
        self.config = parent / "public config.json"
        self.host = parent / "host-fingerprint.bin"
        self.admission = parent / "lab-admission.json"
        self.host.write_bytes(b"SYNTHETIC TEST ONLY - never a measured host\n")
        self.manifest = {
            "schema": "APP02B-LAB-ENVIRONMENT-V1",
            "environmentId": "lab-unit-test",
            "hostSha256": digest(self.host.read_bytes()),
            "authorizationRecord": {"reference": "ref-" + "a" * 16, "version": 1,
                                    "sha256": "a" * 64},
            "secretReferences": [{"reference": "ref-" + "b" * 16, "version": 1}],
            "targets": {
                "A": {"targetRef": "ref-" + "c" * 16, "identitySha256": "c" * 64,
                      "registryVersion": 1},
                "B": {"targetRef": "ref-" + "d" * 16, "identitySha256": "d" * 64,
                      "registryVersion": 1},
            },
        }
        put(self.config, self.manifest)
        put(self.admission, {
            "schema": "APP02B-LAB-ADMISSION-V1",
            "evidenceRoot": str(self.root),
            **{key: self.manifest[key] for key in ("environmentId", "hostSha256", "targets")},
        })
        self.git("init", "-q")
        tracked = self.checkout / "synthetic.txt"
        tracked.write_text("Synthetic test checkout", encoding="utf-8")
        self.git("add", ".")
        self.git("commit", "-qm", "Synthetic source fixture")
        self.source = self.git("rev-parse", "HEAD").strip()
        repo = patch.object(context, "REPO", self.checkout)
        repo.start()
        self.addCleanup(repo.stop)

    def git(self, *args):
        return subprocess.run(
            ["git", "-C", str(self.checkout), "-c", "user.name=Lab Unit Test",
             "-c", "user.email=lab-tests@example.invalid", "-c", "commit.gpgsign=false",
             "-c", "core.autocrlf=false", *args],
            check=True, text=True, capture_output=True,
        ).stdout

    def inputs(self):
        return context.collect_inputs(self.config, self.root, self.source, RUN_ID)

    def prepare(self):
        inputs = self.inputs()
        prepare_run(self.root, inputs)
        return inputs

    def append(self, selection="case:B01-01", **options):
        return append_attempt(self.bundle, self.inputs(), selection_ids(selection), **options)

    def install_cli(self):
        for relative in ("tools/app02b_lab", "scripts/lab"):
            shutil.copytree(REPO / relative, self.checkout / relative,
                            ignore=shutil.ignore_patterns("__pycache__"))
        fixture = self.checkout / FIXTURE.relative_to(REPO)
        fixture.parent.mkdir(parents=True)
        shutil.copyfile(FIXTURE, fixture)
        self.git("add", ".")
        self.git("commit", "-qm", "Synthetic CLI integration fixture")
        self.source = self.git("rev-parse", "HEAD").strip()
