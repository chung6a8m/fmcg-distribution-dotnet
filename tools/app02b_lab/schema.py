"""Validate the versioned JSON documents and expose the bundle validator."""

from dataclasses import dataclass
from datetime import datetime
from functools import lru_cache
from pathlib import Path
import re
from typing import TYPE_CHECKING

from .contract import LabError, read_json, require, strict_json

if TYPE_CHECKING:
    from .context import LabRunContext

_SCHEMAS = Path(__file__).with_name("schemas")
_TYPES = {"object": dict, "array": list, "string": str, "integer": int, "null": type(None)}



@dataclass(frozen=True)
class ValidationIssue:
    code: str
    location: str


@dataclass(frozen=True)
class LabValidationReport:
    run: "LabRunContext | None"
    coverage: dict[str, str]
    attempts: tuple[dict, ...]
    issues: tuple[ValidationIssue, ...]
    checkpoint: dict | None = None
    originalGateStatus: str = "NOT_VERIFIED"

    @property
    def status(self) -> str:
        return "FAIL" if self.issues else "VALID"


@lru_cache
def _document(name: str) -> dict:
    return read_json(_SCHEMAS / f"lab-{name}-v1.json")


def _check(value, rule: dict, definitions: dict, location: str) -> None:
    # This deliberately interprets only the keywords used by the bundled V1
    # documents. The three documents are also standard Draft 2020-12 schemas.
    if "$ref" in rule:
        _check(value, definitions[rule["$ref"].removeprefix("#/$defs/")], definitions, location)
    if "anyOf" in rule:
        for option in rule["anyOf"]:
            try:
                _check(value, option, definitions, location)
                break
            except LabError:
                continue
        else:
            raise LabError("SCHEMA_ALTERNATIVE", location)
    if "type" in rule:
        require(type(value) is _TYPES[rule["type"]], "SCHEMA_TYPE", location)
    if "const" in rule:
        expected = rule["const"]
        require(type(value) is type(expected) and value == expected, "SCHEMA_CONSTANT", location)
    if "enum" in rule:
        require(value in rule["enum"], "SCHEMA_ENUM", location)
    if "pattern" in rule:
        require(re.fullmatch(rule["pattern"], value, re.ASCII) is not None,
                "SCHEMA_PATTERN", location)
    if rule.get("format") == "date-time":
        try:
            datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ")
        except ValueError:
            raise LabError("INVALID_UTC", location) from None
    if "minimum" in rule:
        require(value >= rule["minimum"], "SCHEMA_MINIMUM", location)
    if isinstance(value, list):
        require(rule.get("minItems", 0) <= len(value) <= rule.get("maxItems", len(value)),
                "SCHEMA_ARRAY_SIZE", location)
        if rule.get("uniqueItems"):
            require(all(item not in value[:index] for index, item in enumerate(value)),
                    "SCHEMA_DUPLICATE_ITEM", location)
        if "items" in rule:
            for index, item in enumerate(value):
                _check(item, rule["items"], definitions, f"{location}[{index}]")
    if isinstance(value, dict):
        properties = rule.get("properties", {})
        require(set(rule.get("required", ())) <= value.keys(), "SCHEMA_REQUIRED", location)
        if rule.get("additionalProperties") is False:
            require(value.keys() <= properties.keys(), "SCHEMA_UNKNOWN_FIELD", location)
        for key, definition in properties.items():
            if key in value:
                _check(value[key], definition, definitions, f"{location}.{key}")
        if "if" in rule:
            try:
                _check(value, rule["if"], definitions, location)
            except LabError:
                pass
            else:
                _check(value, rule["then"], definitions, location)


def validate_document(value: object, name: str, definition: str | None = None) -> None:
    document = _document(name)
    definitions = document.get("$defs", {})
    _check(value, definitions[definition] if definition else document, definitions,
           definition or name)


def validate_lab_bundle(root: Path, *, checkpoint: dict | None = None) -> LabValidationReport:
    from .bundle import validate_bundle

    return validate_bundle(root, checkpoint=checkpoint)


__all__ = ["LabError", "LabValidationReport", "strict_json", "validate_document", "validate_lab_bundle"]
