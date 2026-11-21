"""LAB-D02 fixed Compose model and fail-closed ownership reconciliation."""

from ipaddress import IPv4Network
from pathlib import Path
import re

from .contract import read_json, require
from .docker_preflight import network
from .environment import PRIVATE_NETWORKS


TEMPLATE = Path(__file__).resolve().parents[2] / "deploy/lab/docker-wsl2/compose.yaml"
PROJECT_LABEL = "io.fmcg-dms.lab-d02.project"
NONCE_LABEL = "io.fmcg-dms.lab-d02.nonce"
SERVICES = ("api", "worker")
PROJECT = re.compile(r"[a-z][a-z0-9_-]{2,56}")
IMAGE = re.compile(r"[a-z0-9][a-z0-9._-]*(?::[0-9]{1,5})?(?:/[a-z0-9][a-z0-9._-]*)+@sha256:[0-9a-f]{64}")
IMAGE_DEFAULTS = {
    "PATH": r"/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
    "APP_UID": r"1654",
    "ASPNETCORE_HTTP_PORTS": r"8080",
    "DOTNET_RUNNING_IN_CONTAINER": r"true",
    "DOTNET_VERSION": r"[0-9]+\.[0-9]+\.[0-9]+",
    "ASPNET_VERSION": r"[0-9]+\.[0-9]+\.[0-9]+",
}


def resources(d01_plan, nonce):
    project = d01_plan.get("project")
    require(isinstance(project, str) and PROJECT.fullmatch(project), "D02_INVALID_PROJECT")
    require(isinstance(nonce, str) and re.fullmatch(r"(?:[0-9a-f]{32}|[0-9a-f]{64})", nonce)
            and any(digit != "0" for digit in nonce), "D02_INVALID_NONCE")
    names = [f"{project}_{role}" for role in ("app", "data", "vault")]
    networks = d01_plan.get("networks")
    require(isinstance(networks, list) and len(networks) == len(names)
            and all(isinstance(item, dict) and set(item) == {"name", "subnet"}
                    and isinstance(item["name"], str) for item in networks), "D02_NETWORK_SCOPE")
    by_name = {item["name"]: item["subnet"] for item in networks}
    require(set(by_name) == set(names), "D02_NETWORK_SCOPE")
    parsed = []
    for name in names:
        subnet = network(by_name[name], "D02_NETWORK_SCOPE")
        require(str(subnet) == by_name[name] and 16 <= subnet.prefixlen <= 29
                and any(subnet.subnet_of(private) for private in PRIVATE_NETWORKS)
                and not any(subnet.overlaps(other) for other in parsed), "D02_NETWORK_SCOPE")
        parsed.append(subnet)

    volumes = [f"{project}_{role}" for role in ("sql", "vault")]
    chosen = d01_plan.get("volumes")
    require(isinstance(chosen, list) and all(isinstance(item, str) for item in chosen)
            and sorted(chosen) == sorted(volumes), "D02_VOLUME_SCOPE")
    ports = d01_plan.get("ports")
    require(isinstance(ports, list) and 1 <= len(ports) <= 16
            and all(type(port) is int and 1024 <= port <= 65535 for port in ports)
            and len(set(ports)) == len(ports), "D02_PORT_SCOPE")
    return {"project": project, "nonce": nonce,
            "networks": [{"name": name, "subnet": str(subnet)} for name, subnet in zip(names, parsed)],
            "volumes": volumes, "ports": sorted(ports)}


def labels(spec):
    return {PROJECT_LABEL: spec["project"], NONCE_LABEL: spec["nonce"]}


def container_name(spec, role):
    return f"{spec['project']}-{role}"


def compose(spec, images):
    require(isinstance(images, dict) and set(images) == set(SERVICES)
            and all(isinstance(ref, str) and IMAGE.fullmatch(ref)
                    and not ref.endswith("0" * 64) for ref in images.values())
            and images["api"] != images["worker"], "D02_IMAGE_SCOPE")
    model = read_json(TEMPLATE)
    model["name"] = spec["project"]
    model["networks"]["app"]["name"] = f"{spec['project']}_app"
    for role in SERVICES:
        model["services"][role].update(image=images[role], container_name=container_name(spec, role),
                                      labels=labels(spec))
    return model


def owned_objects(rows, names, expected_labels, *, containers=False):
    """Return exact owned objects, rejecting ambiguous or foreign scoped names."""
    require(isinstance(rows, list), "D02_INVENTORY_INVALID")
    found = {}
    for row in rows:
        require(isinstance(row, dict), "D02_INVENTORY_INVALID")
        name = row.get("Name")
        if containers and isinstance(name, str):
            name = name.removeprefix("/")
        require(isinstance(name, str) and name in names and name not in found, "D02_FOREIGN_RESOURCE")
        config = row.get("Config") if containers else row
        owned = config.get("Labels") if isinstance(config, dict) else None
        require(isinstance(owned, dict) and all(owned.get(key) == value for key, value in expected_labels.items()),
                "D02_FOREIGN_RESOURCE")
        found[name] = row
    return found


def _validate_environment(actual, expected):
    require(isinstance(actual, list), "D02_CONTAINER_DRIFT")
    values = {}
    for assignment in actual:
        require(isinstance(assignment, str) and "=" in assignment, "D02_CONTAINER_DRIFT")
        key, value = assignment.split("=", 1)
        require(key not in values, "D02_CONTAINER_DRIFT")
        values[key] = value
    require(all(values.pop(key, None) == value for key, value in expected.items())
            and all(key in IMAGE_DEFAULTS and re.fullmatch(IMAGE_DEFAULTS[key], value)
                    for key, value in values.items()), "D02_CONTAINER_DRIFT")


def _validate_container(row, role, service, project, app_network, image_id):
    config, host, state, settings = (row.get(key) for key in ("Config", "HostConfig", "State", "NetworkSettings"))
    require(all(isinstance(section, dict) for section in (config, host, state, settings)), "D02_INVENTORY_INVALID")
    tags = config["Labels"]
    require(tags.get("com.docker.compose.project") == project
            and tags.get("com.docker.compose.service") == role
            and tags.get("com.docker.compose.oneoff", "False") == "False", "D02_CONTAINER_DRIFT")
    require(row.get("Image") == image_id and config.get("Image") == service["image"]
            and config.get("User") == service["user"]
            and config.get("Entrypoint") == service["entrypoint"]
            and config.get("WorkingDir") == service["working_dir"]
            and config.get("Healthcheck") == {"Test": ["NONE"]}
            and not any(config.get(key) for key in ("Cmd", "Volumes", "OpenStdin", "Tty")), "D02_CONTAINER_DRIFT")
    _validate_environment(config.get("Env"), service["environment"])
    require(state.get("Status") == "created" and state.get("Running") is False, "D02_CONTAINER_ALREADY_STARTED")
    security = host.get("SecurityOpt")
    require(isinstance(security, list) and len(security) == 1
            and security[0] in ("no-new-privileges:true", "no-new-privileges=true", "no-new-privileges")
            and host.get("CapDrop") == ["ALL"] and host.get("ReadonlyRootfs") is True
            and host.get("Privileged") is False and host.get("PublishAllPorts") is False
            and host.get("NetworkMode") == app_network, "D02_CONTAINER_DRIFT")
    require(not any(host.get(key) for key in (
        "Binds", "Mounts", "VolumesFrom", "Tmpfs", "PortBindings", "CapAdd", "Devices", "DeviceRequests",
        "DeviceCgroupRules", "PidMode", "UsernsMode", "UTSMode", "Links", "ExtraHosts", "Sysctls",
        "Dns", "DnsOptions", "DnsSearch",
    )) and host.get("IpcMode") in (None, "", "private")
            and host.get("Runtime") in (None, "", "runc")
            and row.get("Mounts") == [], "D02_CONTAINER_DRIFT")
    restart = host.get("RestartPolicy")
    require(isinstance(restart, dict) and restart.get("Name") in ("", "no")
            and restart.get("MaximumRetryCount") == 0, "D02_CONTAINER_DRIFT")
    attached = settings.get("Networks")
    published = settings.get("Ports") or {}
    require(isinstance(attached, dict) and set(attached) <= {app_network}
            and isinstance(published, dict) and not any(published.values()), "D02_CONTAINER_DRIFT")
    if app_network in attached:
        endpoint = attached[app_network]
        require(isinstance(endpoint, dict) and not endpoint.get("IPAMConfig")
                and not endpoint.get("Links"), "D02_CONTAINER_DRIFT")
        aliases = endpoint.get("Aliases")
        require(aliases is None or isinstance(aliases, list) and all(
            alias in (role, service["container_name"]) for alias in aliases), "D02_CONTAINER_DRIFT")
    identifier = row.get("Id")
    require(isinstance(identifier, str) and re.fullmatch(r"[0-9a-f]{64}", identifier), "D02_INVENTORY_INVALID")
    return identifier


def _validate_network(row, subnet, expected_container_names):
    ipam = row.get("IPAM")
    # Docker Desktop can report the default IPv4-only address families explicitly.
    default_address_families = {
        "com.docker.network.enable_ipv4": "true",
        "com.docker.network.enable_ipv6": "false",
    }
    require(row.get("Driver") == "bridge" and row.get("Scope") == "local" and row.get("Internal") is True
            and row.get("EnableIPv4", True) is True
            and not any(row.get(key) for key in ("Attachable", "Ingress", "EnableIPv6")),
            "D02_NETWORK_DRIFT")
    require(row.get("Options") in (None, {}, default_address_families), "D02_NETWORK_DRIFT")
    require(isinstance(ipam, dict) and ipam.get("Driver") == "default" and not ipam.get("Options"),
            "D02_NETWORK_DRIFT")
    config = ipam.get("Config")
    require(isinstance(config, list) and len(config) == 1 and isinstance(config[0], dict)
            and config[0].get("Subnet") == subnet
            and not (set(config[0]) - {"Subnet", "Gateway"}), "D02_NETWORK_DRIFT")
    gateway = config[0].get("Gateway")
    require(gateway in (None, "", str(IPv4Network(subnet).network_address + 1)), "D02_NETWORK_DRIFT")
    endpoints = row.get("Containers") or {}
    require(isinstance(endpoints, dict) and all(
        identifier in expected_container_names and isinstance(endpoint, dict)
        and endpoint.get("Name") == expected_container_names[identifier]
        for identifier, endpoint in endpoints.items()), "D02_FOREIGN_NETWORK_ENDPOINT")


def validate_state(spec, compose_doc, state, image_ids, *, complete=False):
    project = spec["project"]
    app_network = f"{project}_app"
    subnets = {row["name"]: row["subnet"] for row in spec["networks"]}
    container_roles = {container_name(spec, role): role for role in SERVICES}
    require(isinstance(state, dict) and isinstance(image_ids, dict) and set(image_ids) == set(SERVICES),
            "D02_INVENTORY_INVALID")
    owned = labels(spec)
    containers = owned_objects(state.get("containers"), container_roles, owned, containers=True)
    networks = owned_objects(state.get("networks"), subnets, owned)
    volumes = owned_objects(state.get("volumes"), spec["volumes"], owned)
    users = state.get("volumeUsers")
    require(isinstance(users, dict) and set(users) == set(volumes), "D02_INVENTORY_INVALID")
    for name, volume in volumes.items():
        require(volume.get("Scope") == "local" and volume.get("Driver") == "local" and not volume.get("Options"),
                "D02_VOLUME_DRIFT")
        require(users[name] == [], "D02_FOREIGN_VOLUME_USER")

    require(not containers or app_network in networks, "D02_NETWORK_DRIFT")
    endpoint_names = {}
    for name, row in containers.items():
        role = container_roles[name]
        identifier = _validate_container(row, role, compose_doc["services"][role], project, app_network,
                                         image_ids[role])
        require(identifier not in endpoint_names, "D02_INVENTORY_INVALID")
        endpoint_names[identifier] = name
    for name, row in networks.items():
        _validate_network(row, subnets[name], endpoint_names if name == app_network else {})
    if complete:
        require(set(containers) == set(container_roles) and set(networks) == set(subnets)
                and set(volumes) == set(spec["volumes"]), "D02_INCOMPLETE")
