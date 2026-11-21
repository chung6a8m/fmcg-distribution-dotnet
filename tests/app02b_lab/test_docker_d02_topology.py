import copy
import unittest

from tools.app02b_lab.contract import LabError
from tools.app02b_lab.docker_d02_topology import compose, labels, resources, validate_state


PROJECT = "app02b-docker-lab"
NONCE = "ab" * 16
IMAGES = {
    "api": "ghcr.io/example/d02-api@sha256:" + "c" * 64,
    "worker": "ghcr.io/example/d02-worker@sha256:" + "d" * 64,
}
IMAGE_IDS = {"api": "sha256:" + "1" * 64, "worker": "sha256:" + "2" * 64}


def host_plan():
    return {
        "project": PROJECT,
        "networks": [
            {"name": f"{PROJECT}_app", "subnet": "172.29.40.0/24"},
            {"name": f"{PROJECT}_data", "subnet": "172.29.41.0/24"},
            {"name": f"{PROJECT}_vault", "subnet": "172.29.42.0/24"},
        ],
        "volumes": [f"{PROJECT}_sql", f"{PROJECT}_vault"],
        "ports": [18443],
    }


def inspected_state(spec, document):
    owned = labels(spec)
    networks = [
        {"Name": network["name"], "Driver": "bridge", "Scope": "local", "Internal": True,
         "Ingress": False, "Attachable": False, "EnableIPv6": False, "Options": {},
         "IPAM": {"Driver": "default", "Config": [{"Subnet": network["subnet"]}]},
         "Labels": owned, "Containers": {}}
        for network in spec["networks"]
    ]
    volumes = [
        {"Name": name, "Driver": "local", "Scope": "local", "Options": None, "Labels": owned}
        for name in spec["volumes"]
    ]
    containers = []
    for service, container_id in (("api", "3" * 64), ("worker", "4" * 64)):
        settings = document["services"][service]
        containers.append(
            {"Id": container_id, "Name": f"/{PROJECT}-{service}", "Image": IMAGE_IDS[service],
             "Config": {
                 "Labels": dict(owned, **{"com.docker.compose.project": PROJECT,
                                           "com.docker.compose.service": service}),
                 "Image": IMAGES[service], "User": "1654:1654", "Entrypoint": settings["entrypoint"],
                 "WorkingDir": "/app",
                 "Cmd": [], "Env": ["DOTNET_RUNNING_IN_CONTAINER=true"] +
                 [f"{key}={value}" for key, value in settings["environment"].items()],
                 "Healthcheck": {"Test": ["NONE"]}},
             "HostConfig": {
                 "ReadonlyRootfs": True, "Privileged": False, "NetworkMode": f"{PROJECT}_app",
                 "SecurityOpt": ["no-new-privileges:true"], "PortBindings": {}, "PublishAllPorts": False,
                 "CapDrop": ["ALL"], "CapAdd": None, "Binds": None, "Tmpfs": None, "Mounts": None,
                 "RestartPolicy": {"Name": "no", "MaximumRetryCount": 0}},
             "NetworkSettings": {"Networks": {f"{PROJECT}_app": {"Aliases": [service]}},
                                 "Ports": {"8080/tcp": None} if service == "api" else {}},
             "Mounts": [], "State": {"Status": "created", "Running": False}})
    return {"containers": containers, "networks": networks, "volumes": volumes,
            "volumeUsers": {name: [] for name in spec["volumes"]}}


class DockerD02TopologyTests(unittest.TestCase):
    def setUp(self):
        self.spec = resources(host_plan(), NONCE)
        self.doc = compose(self.spec, IMAGES)

    def test_manifest_stages_foundation_with_no_exposure_or_live_dependencies(self):
        self.assertEqual(self.spec["ports"], [18443])
        self.assertEqual(set(self.doc["services"]), {"api", "worker"})
        self.assertEqual(self.doc["networks"], {
            "app": {"name": f"{PROJECT}_app", "external": True},
        })
        self.assertNotIn("volumes", self.doc)
        for role, service in self.doc["services"].items():
            with self.subTest(role=role):
                self.assertEqual(service["container_name"], f"{PROJECT}-{role}")
                self.assertEqual(service["labels"], {
                    "io.fmcg-dms.lab-d02.project": PROJECT,
                    "io.fmcg-dms.lab-d02.nonce": NONCE,
                })
                self.assertEqual(service["image"], IMAGES[role])
                self.assertEqual(service["environment"]["Runtime__Admission"], "NO_GO")
                self.assertEqual(service["environment"]["Runtime__Mode"], "FOUNDATION_ONLY")
                self.assertFalse(set(service) & {
                    "build", "ports", "volumes", "privileged", "network_mode", "cap_add", "secrets",
                })

    def test_stage_requires_exact_names_and_nonzero_nonce(self):
        foreign = host_plan()
        foreign["volumes"][1] = "operator-owned"
        with self.assertRaises(LabError):
            resources(foreign, NONCE)
        overlap = host_plan()
        overlap["networks"][0]["subnet"] = overlap["networks"][1]["subnet"]
        with self.assertRaises(LabError):
            resources(overlap, NONCE)
        with self.assertRaises(LabError):
            resources(host_plan(), "0" * 32)
        with self.assertRaises(LabError):
            compose(self.spec, dict(IMAGES, api="ghcr.io/example/d02-api:latest"))

    def test_partial_resources_can_converge_without_adopting_foreign_objects(self):
        state = inspected_state(self.spec, self.doc)
        state["containers"] = state["containers"][:1]
        unused = state["volumes"].pop()
        del state["volumeUsers"][unused["Name"]]
        validate_state(self.spec, self.doc, state, IMAGE_IDS)
        with self.assertRaisesRegex(LabError, "D02_INCOMPLETE"):
            validate_state(self.spec, self.doc, state, IMAGE_IDS, complete=True)
        validate_state(self.spec, self.doc, inspected_state(self.spec, self.doc), IMAGE_IDS, complete=True)

    def test_rejects_volume_consumers_wrong_labels_and_case_variants(self):
        clean = inspected_state(self.spec, self.doc)
        cases = []
        state = copy.deepcopy(clean)
        state["volumeUsers"][f"{PROJECT}_sql"].append("f" * 64)
        cases.append(state)
        state = copy.deepcopy(clean)
        del state["volumes"][0]["Labels"]
        cases.append(state)
        state = copy.deepcopy(clean)
        state["networks"][0]["Name"] = f"{PROJECT.upper()}_app"
        cases.append(state)
        state = copy.deepcopy(clean)
        state["networks"][1]["Containers"]["f" * 64] = {"Name": "foreign"}
        cases.append(state)
        state = copy.deepcopy(clean)
        state["volumes"][0]["Options"] = {"type": "none", "device": "/"}
        cases.append(state)
        for state in cases:
            with self.subTest(state=state), self.assertRaises(LabError):
                validate_state(self.spec, self.doc, state, IMAGE_IDS)

    def test_rejects_container_drift_before_any_retry(self):
        mutations = [
            ("State", "Status", "running"),
            ("HostConfig", "NetworkMode", "host"),
            ("HostConfig", "Privileged", True),
            ("HostConfig", "PortBindings", {"8080/tcp": [{"HostIp": "", "HostPort": "8080"}]}),
            ("HostConfig", "Binds", ["/var/run/docker.sock:/var/run/docker.sock"]),
            ("HostConfig", "Dns", ["10.0.0.1"]),
            ("HostConfig", "RestartPolicy", {"Name": "always", "MaximumRetryCount": 0}),
            ("Config", "Entrypoint", ["/bin/sh"]),
            ("Config", "WorkingDir", "/tmp"),
            ("Config", "Env", ["Runtime__Admission=GO"]),
            ("NetworkSettings", "Networks", {f"{PROJECT}_app": {}, "wrong_network": {}}),
            ("NetworkSettings", "Networks", {f"{PROJECT}_app": {"Aliases": ["vault"]}}),
        ]
        for group, key, value in mutations:
            with self.subTest(group=group, key=key):
                state = inspected_state(self.spec, self.doc)
                state["containers"][0][group][key] = value
                with self.assertRaises(LabError):
                    validate_state(self.spec, self.doc, state, IMAGE_IDS)
        state = inspected_state(self.spec, self.doc)
        state["containers"][0]["Image"] = "sha256:" + "f" * 64
        with self.assertRaises(LabError):
            validate_state(self.spec, self.doc, state, IMAGE_IDS)


if __name__ == "__main__":
    unittest.main()
