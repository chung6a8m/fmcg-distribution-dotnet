"""Read the original B.9 fixture without changing its bytes or case semantics."""

from dataclasses import dataclass
from pathlib import Path

from .contract import digest, read_bytes, require, strict_json

REPO = Path(__file__).resolve().parents[2]
FIXTURE = REPO / "tools/app02b9/expected-v1.json"
FIXTURE_SHA256 = "9d7f89e4ccc941ba2a879509f32f7f4e74ca1b4fb5aad748e9a7e7851e34df81"
CASE_IDS = tuple(f"B{gate:02d}-{case:02d}" for gate in range(1, 6) for case in range(1, 7))
GATE_IDS = tuple(f"G-B{gate:02d}" for gate in range(1, 6))
RUNTIME_PROBES = {
    "RUNTIME-01": ("EXACT_RUNTIME_SOURCE_AND_PRIVATE_INGRESS", "NO_UNEXPECTED_SOCKET_OR_RUNTIME_SWITCH",
                   ("CONFIG", "NETWORK")),
    "RUNTIME-02": ("RUNTIME_AUTHORITY_AND_BYPASS_DENIED", "NO_UNAUTHORIZED_RUNTIME_ACCESS",
                   ("HTTP", "NETWORK", "VAULT")),
}


@dataclass(frozen=True)
class CaseDefinition:
    caseId: str
    expected: str
    negativeInvariant: str
    requiredKinds: tuple[str, ...]


def parse_catalog(data: bytes) -> dict[str, CaseDefinition]:
    fixture = strict_json(data)
    require(isinstance(fixture, dict) and
            set(fixture) == {"schema", "authority", "caseCount", "cases"} and
            fixture["schema"] == "APP02B9-FROZEN-FIXTURE-V1" and
            fixture["authority"] == "docs/security/app-02b1-acceptance-evidence.md" and
            type(fixture["caseCount"]) is int and fixture["caseCount"] == 30,
            "FIXTURE_SCHEMA")
    cases = fixture["cases"]
    require(isinstance(cases, dict) and set(cases) == set(CASE_IDS), "FIXTURE_CASE_IDS")
    require(digest(data) == FIXTURE_SHA256, "FIXTURE_BYTES_CHANGED")
    # The exact byte pin also binds every expected symbol, invariant and kind.
    return {
        key: CaseDefinition(key, cases[key]["expected"], cases[key]["negativeInvariant"],
                            tuple(cases[key]["requiredKinds"]))
        for key in CASE_IDS
    }


def load_catalog(fixture: Path) -> dict[str, CaseDefinition]:
    return parse_catalog(read_bytes(fixture))


def coverage_catalog(cases: dict[str, CaseDefinition]) -> dict[str, CaseDefinition]:
    return cases | {key: CaseDefinition(key, *values) for key, values in RUNTIME_PROBES.items()}
