from pathlib import Path
import unittest

from tools.app02b_lab.contract import LabError
from tools.app02b_lab import docker_d04 as d04


class SqlPlanTests(unittest.TestCase):
    def test_private_model_never_contains_bootstrap_password(self):
        scope = d04.scope("lab-sql", "a" * 32)
        model = d04.compose(scope, Path("leaf.pem"), Path("leaf.key"), Path("private-secret"))
        service = model["services"]["sql"]
        self.assertNotIn("ports", service)
        self.assertEqual(list(model["networks"]), ["data"])
        self.assertEqual(model["volumes"]["data"]["name"], "lab-sql_sql")
        self.assertNotEqual(model["volumes"]["backup"]["name"], model["volumes"]["data"]["name"])
        self.assertNotIn("MSSQL_SA_PASSWORD", service["environment"])
        self.assertEqual(service["environment"]["MSSQL_PID"], "Developer")

    def test_database_allowlist_reuses_b4_tenant_naming(self):
        names = d04.scope("lab-sql", "a" * 32)["databases"]
        self.assertEqual(len(set(names.values())), 5)
        self.assertTrue(names["tenantA"].startswith("FmcgPpT_"))
        self.assertTrue(names["tenantB"].startswith("FmcgPpT_"))

    def test_rejects_invalid_names_and_nonce(self):
        for project, nonce in (("foreign/target", "a" * 32), ("lab-sql", "0" * 32), ("lab-sql", "../foreign")):
            with self.assertRaises(LabError):
                d04.scope(project, nonce)

    def test_synthetic_trust_receipt_cannot_enroll_live(self):
        with self.assertRaisesRegex(LabError, "D04_D03_TRUST_REQUIRED"):
            d04.require_trust({"schema": "APP02B-LAB-DOCKER-VAULT-PEER-RESULT-V1",
                               "status": "ROLE_AUDIT_OBSERVED",
                               "vantage": "LINUX_RUNNER_PRIVATE_BRIDGE_IP"},
                              "a" * 64, "b" * 40)
