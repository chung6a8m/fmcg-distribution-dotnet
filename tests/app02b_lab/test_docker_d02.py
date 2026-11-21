from datetime import datetime, timezone
import unittest
from unittest.mock import Mock, patch

from tools.app02b_lab.contract import LabError, canonical_json, digest
from tools.app02b_lab import docker_d02 as d02

SHA = "a" * 64
NONCE = "b" * 32
NOW = datetime(2026, 11, 21, tzinfo=timezone.utc)


class DockerD02BoundaryTests(unittest.TestCase):
    def test_enrollment_requires_independent_digest_and_exact_plan(self):
        envelope = {"schema": "APP02B-LAB-DOCKER-ENROLLMENT-V1", "operation": "CREATE_STOPPED_FOUNDATION",
                    "nonce": NONCE, "planSha256": SHA, "expiresUtc": "2026-11-22T00:00:00Z"}
        raw = canonical_json(envelope)
        self.assertIsNone(d02.authorize(raw, digest(raw), SHA, NONCE, now=NOW))
        for approved, plan, nonce, now in (
            ("", SHA, NONCE, NOW), ("f" * 64, SHA, NONCE, NOW), (digest(raw), "f" * 64, NONCE, NOW),
            (digest(raw), SHA, "f" * 32, NOW), (digest(raw), SHA, NONCE, datetime(2026, 11, 22, tzinfo=timezone.utc)),
        ):
            with self.subTest(approved=approved, plan=plan, nonce=nonce, now=now):
                with self.assertRaises(LabError):
                    d02.authorize(raw, approved, plan, nonce, now=now)

    def test_fresh_guard_is_rechecked_after_first_mutation(self):
        docker = Mock()
        network = {"name": "app02b-docker-lab_app", "subnet": "172.29.40.0/24"}
        spec = {"project": "app02b-docker-lab", "nonce": NONCE, "networks": [network],
                "volumes": ["app02b-docker-lab_sql"], "ports": [18443]}
        state = {"volumes": [], "networks": [], "containers": [], "volumeUsers": {}}
        docker.state.return_value = state

        def create_network(*arguments):
            self.assertEqual(arguments[:2], ("network", "create"))
            state["networks"].append({"Name": network["name"]})

        def guard():
            if state["networks"]:
                raise LabError("D02_FRESH_D01_BLOCKED")

        docker.run.side_effect = create_network
        with patch("tools.app02b_lab.docker_d02_topology.validate_state"):
            with self.assertRaisesRegex(LabError, "^D02_FRESH_D01_BLOCKED$"):
                d02.apply_change(docker, spec, {}, {}, ensure_current=guard)
        docker.run.assert_called_once()

    def test_missing_storage_from_reviewed_inventory_refuses_before_mutation(self):
        docker = Mock()
        docker.state.return_value = {"volumes": [], "networks": [], "containers": [], "volumeUsers": {}}
        spec = {"project": "app02b-docker-lab", "nonce": NONCE,
                "networks": [], "volumes": ["app02b-docker-lab_sql"], "ports": [18443]}
        with self.assertRaises(LabError):
            d02.apply_change(docker, spec, {}, {}, ensure_current=lambda: None,
                             reviewed_volumes={"app02b-docker-lab_sql"})
        docker.run.assert_not_called()

    def test_pinned_app_disappearance_or_replacement_refuses_before_mutation(self):
        project = "app02b-docker-lab"
        published = {"name": project + "_app", "subnet": "172.29.40.0/24"}
        spec = {"project": project, "nonce": NONCE, "networks": [published], "volumes": [], "ports": []}
        for networks in ([], [{"Name": published["name"], "Id": "2"*64}]):
            with self.subTest(networks=networks):
                docker = Mock()
                docker.state.return_value = {"volumes": [], "networks": networks, "containers": [], "volumeUsers": {}}
                with self.assertRaisesRegex(LabError, "^D02_RESUME_NETWORK_CHANGED$"):
                    d02.apply_change(docker, spec, {}, {}, ensure_current=lambda: None,
                                     resume_app_network_id="1"*64)
                docker.run.assert_not_called()

    def test_fresh_inventory_checks_pinned_app_before_excluding_it_from_conflicts(self):
        app, pinned, subnet = "demo_app", "1"*64, "172.29.40.0/24"
        baseline = {"project": "demo", "hostRef": "h", "engineRef": "e",
                    "networks": [{"name": app, "subnet": subnet}],
                    "resumeD02": {"appNetworkId": pinned, "nonce": NONCE}}
        network = {"id": pinned, "name": app, "subnets": [subnet]}
        host = {"projects": [], "volumes": [], "containers": [], "networks": [network]}
        state = {"containers": [], "volumes": [], "networks": [{"Name": app, "Id": pinned}]}
        with (patch.object(d02.d01, "identity_refs", return_value=("h", "e")),
              patch.object(d02, "source_info", return_value=("a"*40, "CLEAN")),
              patch.object(d02.d01, "evaluate", return_value={"checks": {"PORTS": "PASS"}, "issueCodes": []})
              as evaluate):
            self.assertEqual("PASS", d02.require_fresh_host(baseline, {"d01PlanSha256": SHA}, host, state))
            evaluated_plan, inventory, *_ = evaluate.call_args.args
            self.assertNotIn("resumeD02", evaluated_plan)
            self.assertEqual([], inventory["networks"])
            host["networks"] = [{**network, "id": "2"*64}]
            with self.assertRaisesRegex(LabError, "^D02_RESUME_NETWORK_CHANGED$"):
                d02.require_fresh_host(baseline, {"d01PlanSha256": SHA}, host, state)
            evaluate.assert_called_once()

    def test_preflight_rejects_ready_report_from_another_source_or_plan(self):
        plan = {"sourceHead": "a"*40, "hostRef": "1"*64, "engineRef": "2"*64}
        report = {"schema": "APP02B-LAB-DOCKER-HOST-PREFLIGHT-V1", "readiness": "READY",
                  "authorization": "NOT_EVALUATED", "sourceState": "CLEAN", "sourceHead": plan["sourceHead"],
                  "hostRef": plan["hostRef"], "engineRef": plan["engineRef"], "planSha256": SHA,
                  "issueCodes": [], "checks": {"SOURCE": "PASS"}}
        d02.require_baseline(report, plan, SHA, (plan["sourceHead"], "CLEAN"))
        with self.assertRaises(LabError):
            d02.require_baseline(report, plan, SHA, ("b"*40, "CLEAN"))
        with self.assertRaises(LabError):
            d02.require_baseline(report, plan, "f"*64, (plan["sourceHead"], "CLEAN"))
