"""Verify the four execution job results reported by GitHub Actions."""

import json
import os


REQUIRED_JOBS = ("core_sql", "web_sales", "app03", "distribution")


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate key {key!r}")
        result[key] = value
    return result


def reject_non_json_literal(literal):
    raise ValueError(f"unexpected literal {literal}")


def evaluate_needs(raw_needs: str | None) -> tuple[bool, list[str]]:
    if raw_needs is None:
        return False, ["APP00A_NEEDS_JSON is missing"]

    try:
        needs = json.loads(
            raw_needs,
            object_pairs_hook=unique_object,
            parse_constant=reject_non_json_literal,
        )
    except (ValueError, RecursionError) as error:
        return False, [f"APP00A_NEEDS_JSON contains invalid JSON ({error})"]

    if not isinstance(needs, dict):
        return False, ["APP00A_NEEDS_JSON must be a JSON object keyed by job ID"]

    messages = []
    extra_jobs = sorted(needs.keys() - set(REQUIRED_JOBS))
    if extra_jobs:
        messages.append(f"Unexpected jobs: {', '.join(repr(job) for job in extra_jobs)}")
    passed = not extra_jobs

    for job_id in REQUIRED_JOBS:
        if job_id not in needs:
            status = "missing job"
        elif not isinstance(needs[job_id], dict):
            status = "job entry must be an object"
        elif "result" not in needs[job_id]:
            status = "missing result"
        else:
            result = needs[job_id]["result"]
            status = (
                result
                if result in ("success", "failure", "cancelled", "skipped")
                else f"invalid result {result!r}"
            )

        messages.append(f"{job_id}: {status}")
        passed = passed and status == "success"

    return passed, messages


def main() -> int:
    passed, messages = evaluate_needs(os.environ.get("APP00A_NEEDS_JSON"))
    for message in messages:
        print(message)
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
