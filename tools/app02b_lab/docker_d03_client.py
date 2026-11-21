"""Bounded HTTPS Vault operations and redacted LAB-D03 audit correlation."""
from http.client import HTTPSConnection, HTTPException
import re
import ssl
import time
from urllib.parse import urlsplit
from uuid import uuid4

from .contract import LabError, canonical_json, digest, require, strict_json

AUDIT_HEADER = "X-Lab-D03-Probe"
AUDIT_PATH = "lab-file"
AUDIT_FILE = "/vault/logs/lab.jsonl"
ARN = re.compile(r"[a-zA-Z0-9._-]{8,256}", re.ASCII)
ROLE_PATHS = {role: f"preprod/data/runtime/{role}" for role in ("api", "worker", "operator")}
POLICIES = {role: "lab-d03-" + role for role in ROLE_PATHS}
RESPONSE_LIMIT = 1_048_576


class Vault:
    def __init__(self, url, ca_pem, *, connect_address=None, leaf_sha=None):
        try:
            target = urlsplit(url)
            self.host, self.port = target.hostname, target.port
        except ValueError:
            raise LabError("D03_UNSAFE_ENDPOINT") from None
        require(target.scheme == "https" and self.host and self.port == 8200
                and target.netloc == f"{self.host}:8200" and not (target.path or target.query or target.fragment)
                and re.fullmatch(r"[a-z0-9][a-z0-9.-]{0,252}", self.host), "D03_UNSAFE_ENDPOINT")
        # Explicit trust only. No proxy discovery, redirects, default CA fallback, or disabled hostname checks.
        self.context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        self.context.minimum_version = ssl.TLSVersion.TLSv1_2
        self.context.hostname_checks_common_name = False
        if isinstance(ca_pem, bytes):
            self.context.load_verify_locations(cadata=ca_pem.decode("ascii"))
        else:
            self.context.load_verify_locations(cafile=ca_pem)
        self.connect_address = connect_address
        self.leaf_sha = leaf_sha

    def request(self, method, path, *, token=None, payload=None, probe_id=None, timeout=4):
        require(method in ("GET", "POST", "PUT", "DELETE") and re.fullmatch(r"[a-z0-9_/-]+", path)
                and "//" not in path and not path.startswith("/"), "D03_UNSAFE_REQUEST")
        headers = {"Accept": "application/json"}
        if token is not None:
            require(isinstance(token, str) and ARN.fullmatch(token), "D03_INVALID_TOKEN")
            headers["X-Vault-Token"] = token
        if probe_id is not None:
            require(re.fullmatch(r"[a-f0-9]{32}", probe_id), "D03_INVALID_PROBE")
            headers[AUDIT_HEADER] = probe_id
        body = None
        if payload is not None:
            body = canonical_json(payload)
            headers["Content-Type"] = "application/json"
        connection = HTTPSConnection(self.host, self.port, context=self.context, timeout=timeout)
        if self.connect_address is not None:
            # Connect to the disposable CI bridge IP while retaining DNS SNI and verification.
            import socket
            connection._create_connection = lambda _addr, timeout, source: socket.create_connection(
                (self.connect_address, self.port), timeout, source)
        try:
            if self.leaf_sha is not None:
                connection.connect()
                require(digest(connection.sock.getpeercert(binary_form=True)) == self.leaf_sha, "D03_TLS_LEAF_CHANGED")
            connection.request(method, "/v1/" + path, body, headers)
            response = connection.getresponse()
            raw = response.read(RESPONSE_LIMIT + 1)
            require(len(raw) <= RESPONSE_LIMIT, "D03_RESPONSE_LIMIT")
            value = strict_json(raw) if raw else {}
            require(type(value) is dict, "D03_INVALID_VAULT_RESPONSE")
            return response.status, value
        except ssl.SSLCertVerificationError:
            raise LabError("D03_TLS_REJECTED") from None
        except (OSError, HTTPException) as error:
            raise LabError("D03_VAULT_UNREACHABLE", f"{method} {path} {type(error).__name__}") from None
        finally:
            connection.close()

    def expect(self, status, method, path, **options):
        observed, value = self.request(method, path, **options)
        require(observed == status, "D03_UNEXPECTED_HTTP_STATUS", f"{method} {path}: {observed}, expected {status}")
        return value

    def cluster(self):
        health = self.expect(200, "GET", "sys/health")
        require(health.get("initialized") is True and health.get("sealed") is False
                and type(health.get("cluster_id")) is str, "D03_CLUSTER_NOT_ACTIVE")
        return health["cluster_id"]


def audit_witness(records, probe_id, path, *, denied):
    responses = {}
    for row in records:
        if type(row) is not dict or row.get("type") not in ("request", "response"):
            continue
        request = row.get("request") or {}
        headers = request.get("headers") or {}
        # Vault lowercases the header and emits an array. Denials need not return a request ID to the caller.
        identifier = request.get("id")
        if request.get("path") != path or (identifier != probe_id and
                headers.get(AUDIT_HEADER.lower()) != [probe_id]):
            continue
        key = (identifier, row["type"])
        if key in responses or not isinstance(identifier, str) or not identifier:
            return False
        responses[key] = row
    ids = {identifier for identifier, _ in responses}
    if len(ids) != 1:
        return False
    identifier, = ids
    request, response = (responses.get((identifier, kind)) for kind in ("request", "response"))
    if request is None or response is None:
        return False
    error = response.get("error")
    return isinstance(error, str) and "permission denied" in error.lower() if denied else not error


def probe_roles(vault, tokens, read_audit):
    require(set(tokens) == set(ROLE_PATHS) and len(set(tokens.values())) == len(ROLE_PATHS),
            "D03_ROLE_TOKENS_REQUIRED")
    cases = []
    for role, token in tokens.items():
        for owner, path in ROLE_PATHS.items():
            probe_id = uuid4().hex
            allowed = role == owner
            value = vault.expect(200 if allowed else 403, "GET", path, token=token, probe_id=probe_id)
            if allowed:
                require(value.get("data", {}).get("data", {}).get("fixture") == "LAB_D03_SYNTHETIC",
                        "D03_WRONG_FIXTURE")
            cases.append((f"{role}_read_{owner}", probe_id, path, not allowed))
    # Vault writes audit synchronously. Short bound allows an independently read volume/peer to catch up.
    for _ in range(10):
        rows = read_audit()
        if all(audit_witness(rows, probe, path, denied=denied) for _, probe, path, denied in cases):
            return {name: {"httpStatus": 403 if denied else 200, "audit": "OBSERVED"}
                    for name, _, _, denied in cases}
        time.sleep(0.2)
    raise LabError("D03_AUDIT_WITNESS_MISSING")


def token_for_role(vault, role, issuer, *, probe_id=None):
    require(role in POLICIES, "D03_INVALID_ROLE")
    result = vault.expect(200, "POST", "auth/token/create/" + POLICIES[role], token=issuer, probe_id=probe_id,
                          payload={"ttl": "5m", "explicit_max_ttl": "5m", "no_default_policy": True,
                                   "policies": [POLICIES[role]]})
    auth = result.get("auth") or {}
    token = auth.get("client_token")
    require(type(token) is str and ARN.fullmatch(token) and auth.get("policies") == [POLICIES[role]]
            and auth.get("renewable") is False and auth.get("token_type") == "service"
            and not auth.get("identity_policies") and type(auth.get("lease_duration")) is int and 0 < auth["lease_duration"] <= 300,
            "D03_ISSUED_ROLE_MISMATCH")
    return token


def verify_role_token(vault, token, policy, probe_id=None):
    reply = vault.expect(200, "GET", "auth/token/lookup-self", token=token, probe_id=probe_id)
    record = reply.get("data") or {}
    require(record.get("policies") == [policy] and not record.get("identity_policies")
            and record.get("type") == "service", "D03_UNEXPECTED_TOKEN_POLICY")


def wait_for_witnesses(read_audit, cases):
    for _ in range(10):
        rows = read_audit()
        if all(audit_witness(rows, probe, path, denied=denied) for probe, path, denied in cases):
            return
        time.sleep(0.2)
    raise LabError("D03_AUDIT_WITNESS_MISSING")


def rotate(vault, role, issuer, previous, store, authorize, read_audit):
    """Persist a verified replacement before revoking the exact approved prior token."""
    require(role in ROLE_PATHS, "D03_INVALID_ROLE")
    path = ROLE_PATHS[role]
    policy = POLICIES[role]
    issuer_probe, prior_probe = uuid4().hex, uuid4().hex
    verify_role_token(vault, issuer, "lab-d03-issuer", issuer_probe)
    verify_role_token(vault, previous, policy, prior_probe)
    vault.expect(200, "GET", path, token=previous)
    for other, denied in ROLE_PATHS.items():
        if other != role:
            vault.expect(403, "GET", denied, token=previous)
    wait_for_witnesses(read_audit, [(probe, "auth/token/lookup-self", False)
                                  for probe in (issuer_probe, prior_probe)])
    authorize()
    issuance = uuid4().hex
    replacement = token_for_role(vault, role, issuer, probe_id=issuance)
    vault.expect(200, "GET", path, token=replacement)
    for other, denied in ROLE_PATHS.items():
        if other != role:
            vault.expect(403, "GET", denied, token=replacement)
    wait_for_witnesses(read_audit, [(issuance, "auth/token/create/" + policy, False)])
    store(replacement)
    authorize()
    revoke, denial = uuid4().hex, uuid4().hex
    vault.expect(204, "POST", "auth/token/revoke-self", token=previous, probe_id=revoke)
    vault.expect(403, "GET", path, token=previous, probe_id=denial)
    wait_for_witnesses(read_audit, [(revoke, "auth/token/revoke-self", False), (denial, path, True)])
    return "ROTATED"
