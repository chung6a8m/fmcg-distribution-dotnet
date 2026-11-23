"""Compare E-SIM-01 synthetic extracts against independently entered expectations."""
import copy
import hashlib
import json
import re
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "docs/roadmap/app-02e-ev-p0"
DATA = ROOT / "e-sim-01"


def require(condition, code):
    if not condition:
        raise ValueError(code)


def unique(values, code):
    values = list(values)
    require(len(values) == len(set(values)), code)


def validate(m, x, e):
    cutoff, as_of = map(datetime.fromisoformat, (m["cutoff"], m["asOf"]))
    require(cutoff < as_of and m["quantityScale"] == m["moneyScale"] == 0, "metadata")
    profiles = m["tenants"]
    for key in ("id", "db", "actor", "seller", "accounting"):
        unique((p[key] for p in profiles), "tenant-" + key)
    require(len(profiles) == 2 and all(p["role"] == "sales" for p in profiles), "profiles")
    a, b = profiles
    require(b["mode"] == "B_NO_CHANNEL" and b["provider"] is None, "no-channel")
    writers = x["writers"]
    unique((r["id"] for r in writers), "writer-duplicate")
    unique(((r["path"], r["principal"]) for r in writers), "writer-path")
    require({r["id"] for r in writers} == {f"W-{i:02}" for i in range(1, 9)}, "writer-scope")
    for writer in writers:
        require(writer["tenant"] == a["id"] and writer["cutoff"] == m["cutoff"]
                and writer["location"] in m["locations"], "writer-binding")
        require(writer["wave"] == ("outside" if writer["location"] == "OUTSIDE" else "in"),
                "writer-wave")
    require(any(r["wave"] == "outside" for r in writers), "outside-writer")

    rows = x["rows"]
    unique((r["id"] for r in rows), "opening-id")
    unique((r["source"] for r in rows), "opening-source")
    keys = [(r["location"], r["account"]) for r in rows]
    unique(keys, "opening-duplicate")
    require(set(keys) == {tuple(k) for k in e["openingKeys"]}, "opening-scope")
    qty = value = 0
    balances = {}
    for r in rows:
        amount = r["amount"]
        require(r["tenant"] == a["id"] and r["location"] in m["locations"]
                and r["cutoff"] == m["cutoff"] and r["currency"] == m["currency"]
                and r["source"].startswith("legacy-snapshot-v1:"), "opening-binding")
        require(amount is None or type(amount) is int and amount >= 0, "amount-precision")
        status = "UNKNOWN" if amount is None else "ASSUMED_ZERO" if amount == 0 else "ASSUMED"
        require(r["status"] == status, "opening-status")
        if r["account"] == "stock":
            q = r["quantity"]
            require(type(q) is int and q >= 0 and r["sku"] == m["sku"] and r["lot"] == m["lot"]
                    and r["unit"] == m["quantityUnit"] and q * m["unitCost"] == amount, "stock-line")
            qty += q
            value += amount
        else:
            require(all(r[k] is None for k in ("quantity", "sku", "lot", "unit")), "balance-unit")
            balances[r["account"]] = amount
    require((qty, value) == (e["stockQuantity"], e["stockValue"]), "stock-totals")
    require(balances == e["balances"], "balances")

    delivery, probe, receipt, posting, rec = (x[k] for k in
        ("delivery", "invoiceProbe", "receipt", "posting", "reconciliation"))
    require(delivery["tenant"] == a["id"] and delivery["seller"] == a["seller"]
            and delivery["invoiceEligible"] is m["invoiceEligible"] is False, "delivery-scope")
    require(probe["delivery"] == receipt["delivery"] == delivery["id"]
            and probe["provider"] == a["provider"] and probe["identity"]
            and probe["initial"] == "UNKNOWN" and probe["lookup"] == "SIMULATED_FOUND",
            "provider-link")
    require(receipt["mapping"] == "mapping-v1" and posting["receipt"] == receipt["id"]
            and posting["ledger"] == a["accounting"] and rec["posting"] == posting["id"]
            and rec["asOf"] == m["asOf"], "posting-link")
    occurred, received, posted = (datetime.fromisoformat(t) for t in
        (delivery["occurredAt"], receipt["receivedAt"], posting["postedAt"]))
    require(occurred <= received <= posted < cutoff
            and posting["period"] == occurred.strftime("%Y-%m"), "posting-time")
    unique((line["account"] for line in posting["lines"]), "posting-duplicate")
    lines = {line["account"]: line["amount"] for line in posting["lines"]}
    require(lines == {"AR": delivery["ar"], "COGS": delivery["cost"]}
            and all(type(n) is int for n in lines.values())
            and delivery["quantity"] * m["unitCost"] == delivery["cost"], "posting-values")
    delta = {k: lines[k] - v for k, v in e["posting"].items()}
    require(delta == e["deltas"] and rec["status"] == "RECONCILED_OK", "reconciliation")
    return {"writers": len(writers), "opening": len(rows), "stockQuantity": qty,
            "stockValue": value, "balances": balances, "posting": lines, "delta": delta}


def main():
    paths = [DATA / f"{name}-v1.json" for name in ("manifest", "extract", "expected")]
    m, x, e = (json.loads(p.read_text(encoding="utf-8")) for p in paths)
    historical = (ROOT / "ev-p0-read-only-evidence-request-v0.1.md").read_text(encoding="utf-8")
    report = (ROOT / "e-sim-01.md").read_text(encoding="utf-8")
    pattern = r"^\| ((?:B-RO|W|O-RO|SPK-RO)-\d\d) \|"
    source_ids = re.findall(pattern, historical, re.MULTILINE)
    mapped_ids = re.findall(pattern, report, re.MULTILINE)
    unique(mapped_ids, "request-duplicate")
    require(len(source_ids) == 31 and set(source_ids) == set(mapped_ids), "request-scope")
    actual = validate(m, x, e)
    mutations = {
        "opening-scope": lambda d: d["rows"].pop(),
        "opening-duplicate": lambda d: d["rows"].append(
            dict(d["rows"][0], id="extra", source="legacy-snapshot-v1:extra")),
        "opening-id": lambda d: d["rows"][1].update(id=d["rows"][0]["id"]),
        "opening-binding": lambda d: d["rows"][0].update(tenant="tenant-b"),
        "opening-status": lambda d: d["rows"][-1].update(amount=0),
        "stock-line": lambda d: d["rows"][0].update(amount=551),
        "stock-totals": lambda d: d["rows"][0].update(quantity=56, amount=560),
        "posting-link": lambda d: d["posting"].update(receipt="incorrect"),
        "posting-time": lambda d: d["posting"].update(period="2026-12"),
        "reconciliation": lambda d: (d["delivery"].update(ar=76),
                                      d["posting"]["lines"][0].update(amount=76)),
    }
    for expected, mutate in mutations.items():
        invalid = copy.deepcopy(x)
        mutate(invalid)
        try:
            validate(m, invalid, e)
        except ValueError as error:
            require(str(error) == expected, f"{expected}: got {error}")
        else:
            raise ValueError(f"missed mutation: {expected}")
    print(json.dumps({"observed": actual, "mappedRequests": len(mapped_ids),
                      "rejected": list(mutations),
                      "sha256": {p.name: hashlib.sha256(p.read_bytes().replace(b"\r\n", b"\n")).hexdigest() for p in paths}},
                     indent=2))


if __name__ == "__main__":
    main()
