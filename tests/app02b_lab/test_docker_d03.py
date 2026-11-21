"""Regression checks for the reviewed Vault plan and credentials."""
from datetime import datetime, timezone
import json
from pathlib import Path
import tempfile
import unittest

from tools.app02b_lab.contract import LabError, canonical_json, digest
from tools.app02b_lab import docker_d03 as d03
from tools.app02b_lab.docker_d03_client import audit_witness, Vault


class VaultPlanTests(unittest.TestCase):
    def test_model_mounts_only_leaf_key_and_cert_without_published_ports(self):
        model = d03.compose("dms-test", "a" * 32, Path("/private/leaf.pem"), Path("/private/leaf.key"))
        service = model["services"]["vault"]
        self.assertNotIn("ports", service)
        self.assertEqual(list(model["networks"]), ["vault"])
        self.assertEqual(service["cap_drop"], ["ALL"])
        self.assertEqual(service["entrypoint"][1:], ["server", "-config=/vault/config/rehearsal.hcl"])
        self.assertEqual(model["volumes"]["data"], {"name": "dms-test_vault", "external": True})
        self.assertNotEqual(model["volumes"]["data"]["name"], model["volumes"]["audit"]["name"])
        self.assertTrue(all(m["read_only"] for m in service["volumes"] if m["type"] == "bind"))

    def test_approval_rejects_scope_drift_and_expiry(self):
        now = datetime(2026, 11, 21, tzinfo=timezone.utc)
        nonce, plan_sha = "a" * 32, "b" * 64
        permit = {"schema": d03.ENROLLMENT, "operation": d03.STAGE, "nonce": nonce,
                  "planSha256": plan_sha, "expiresUtc": "2026-11-22T00:00:00Z"}
        raw = canonical_json(permit)
        d03.authorize(raw, digest(raw), plan_sha, nonce, d03.STAGE, now)
        for sha, plan, operation, instant in (
            ("c" * 64, plan_sha, d03.STAGE, now),
            (digest(raw), "c" * 64, d03.STAGE, now),
            (digest(raw), plan_sha, "ROTATE_API", now),
            (digest(raw), plan_sha, d03.STAGE, datetime(2026, 11, 22, tzinfo=timezone.utc)),
        ):
            with self.subTest(sha=sha, plan=plan, operation=operation, instant=instant):
                with self.assertRaises(LabError):
                    d03.authorize(raw, sha, plan, nonce, operation, instant)

    def test_audit_requires_same_request_and_response_with_denial(self):
        raw = [{"type": kind, "request": {"id": "attempt-id", "path": "preprod/data/runtime/worker"}}
               for kind in ("request", "response")]
        self.assertFalse(audit_witness(raw, "attempt-id", "preprod/data/runtime/worker", denied=True))
        raw[1]["error"] = "permission denied"
        self.assertTrue(audit_witness(raw, "attempt-id", "preprod/data/runtime/worker", denied=True))
        self.assertFalse(audit_witness(raw, "other-id", "preprod/data/runtime/worker", denied=True))

    def test_transport_rejects_plaintext_and_arbitrary_routes(self):
        with self.assertRaisesRegex(LabError, "D03_UNSAFE_ENDPOINT"):
            Vault("http://private-vault:8200", Path("unused-ca.pem"))
        with self.assertRaisesRegex(LabError, "D03_UNSAFE_ENDPOINT"):
            Vault("https://user@private-vault:8200", Path("unused-ca.pem"))
