"""Exercise the Windows demo against two physical LocalDB databases per session.

Run after dotnet build FmcgDms.slnx. Uses only Python's standard library.
"""
import contextlib
import http.cookiejar
import io
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / "src/FmcgDms.Demo/bin/Debug/net10.0/FmcgDms.Demo.dll"
ENV = dict(os.environ, FMCG_DEMO_NO_BROWSER="1")
SOURCE = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip() + "+smoke"


class Browser:
    def __init__(self, launch):
        self.origin = launch.split("/open?")[0]
        self.client = urllib.request.build_opener(
            urllib.request.ProxyHandler({}),
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
        )
        self.get(launch.removeprefix(self.origin))

    def get(self, route):
        with self.client.open(self.origin + route, timeout=15) as response:
            return response.read().decode("utf-8")

    def post(self, route, fields, status=200):
        if "__RequestVerificationToken" not in fields:
            fields["__RequestVerificationToken"] = re.search(
                r'name="__RequestVerificationToken" value="([^"]+)"', self.get("/")
            )[1]
        data = urllib.parse.urlencode(fields).encode("ascii")
        try:
            with self.client.open(self.origin + route, data, timeout=15) as response:
                assert response.status == status
                return response.read().decode("utf-8")
        except urllib.error.HTTPError as error:
            assert error.code == status, error
            return ""

    def role(self, role, tenant="tenant-a"):
        self.post("/identity", {"persona": tenant + ":" + role})

    def report(self):
        return json.loads(self.get("/report.json"))

    def detail(self, step, metric):
        page = self.get(f"/?moc={step}&chiSo={metric}")
        return page.split('id="chi-tiet-doi-soat"', 1)[1].split("</section>", 1)[0]

    def workbook(self):
        with self.client.open(self.origin + "/report.xlsx", timeout=15) as response:
            assert response.headers.get_content_type().endswith("spreadsheetml.sheet")
            return response.read()


@contextlib.contextmanager
def host(mode, run_id):
    with tempfile.TemporaryFile() as log:
        with subprocess.Popen(
            ["dotnet", str(DLL), mode, run_id, SOURCE],
            cwd=ROOT, env=ENV, stdout=log, stderr=subprocess.STDOUT,
        ) as process:
            try:
                deadline = time.monotonic() + 60
                while time.monotonic() < deadline:
                    log.seek(0)
                    status = log.read().decode("utf-8", errors="replace")
                    match = re.search(r"^Open once (http://[^\r\n]+)", status, re.MULTILINE)
                    if match:
                        break
                    if process.poll() is not None:
                        raise RuntimeError(status)
                    time.sleep(0.2)
                else:
                    raise TimeoutError(status)
                yield Browser(match[1])
            finally:
                if process.poll() is None:
                    process.terminate()
                process.wait(timeout=10)


def remove(run_id):
    subprocess.run(["dotnet", str(DLL), "Remove", run_id, SOURCE], cwd=ROOT, env=ENV, check=True)


def numbers(report):
    return [[cell["actual"] for cell in step["cells"]] for step in report["checkpoints"]]

def check_workbook(binary):
    ns = {"s": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
    with zipfile.ZipFile(io.BytesIO(binary)) as workbook:
        assert workbook.testzip() is None
        sheets = [ET.fromstring(workbook.read(f"xl/worksheets/sheet{i}.xml")) for i in range(1, 5)]
        def rows(sheet):
            return [[cell.findtext("s:v", namespaces=ns) or cell.findtext("s:is/s:t", namespaces=ns)
                     for cell in row] for row in sheets[sheet].findall("s:sheetData/s:row", ns)]
        assert len(rows(0)) == 85
        assert len(sheets[0].findall("s:hyperlinks/s:hyperlink", ns)) == 84
        assert any(row[:7] == ["T6_RETURN", "Giá vốn", "450.0000", "450", "0.0000", "450.0000", "0.0000"]
                   for row in rows(0))
        cost = [row for row in rows(1) if row[:2] == ["T6_RETURN", "Giá vốn"]]
        assert {row[2] for row in cost} == {"AC03-3", "AC03-4"} and all(row[8] for row in cost)
        fund = [row for row in rows(1) if row[:2] == ["T5_HANDOVER", "Quỹ"]]
        assert len(fund) == 1 and fund[0][2:5] == ["AC03-6", "FUND", "200.0000"]
        assert any("AC03-5" in row for row in rows(2))
        assert ["Môi trường", "SYNTHETIC_NONPRODUCTION"] in rows(3)


def main():
    complete, sample, fresh = (uuid.uuid4().hex for _ in range(3))
    try:
        with host("AC03Complete", complete) as browser:
            finished = browser.report()
            assert len(numbers(finished)) == 7
            assert all(c["difference"] == 0 for row in finished["checkpoints"] for c in row["cells"])
            assert numbers(finished)[-1] == [55, 0, 45, 550, 0, 450, 15, 0, 375, 100, 0, 200]
            page = browser.get("/")
            assert 'href="/report.xlsx"' in page
            check_workbook(browser.workbook())
            assert 'href="/?moc=T6_RETURN&amp;chiSo=cogs#chi-tiet-doi-soat"' in page
            for checkpoint in finished["checkpoints"]:
                for cell in checkpoint["cells"]:
                    assert "Chi tiết lệch" not in browser.detail(checkpoint["step"], cell["field"]), (checkpoint["step"], cell["field"])
            cogs = browser.detail("T6_RETURN", "cogs")
            assert all(text in cogs for text in ("450", "AC03-3", "AC03-4", "200", "250", "COGS"))
            early_cogs = browser.detail("T3_B20", "cogs")
            assert "AC03-3" in early_cogs and "AC03-4" not in early_cogs
            fund = browser.detail("T5_HANDOVER", "fund")
            assert all(text in fund for text in ("AC03-6", "FUND", "200"))
            assert "chưa có dòng" in browser.detail("T4_A25", "fund")
            assert "Tồn đầu kỳ" in browser.detail("T6_RETURN", "mainQty")
            assert "AC03-3" in browser.detail("T6_RETURN", "arB")
            browser.role("auditor", "tenant-b")
            assert numbers(browser.report()) == numbers(finished)
        with host("AC03Start", sample) as browser:
            assert numbers(browser.report()) == numbers(finished)[:1]
        with host("Fresh", fresh) as browser:
            assert browser.report()["checkpoints"] == []
            form = dict(tenant="tenant-a", customerCode="C-TEST", customerName="Khách kiểm thử",
                        shipToName="Điểm giao", address="Mẫu", skuCode="S-TEST", skuName="Hàng test",
                        uom="EA", warehouseCode="K01", vehicleCode="V01", orderCode="A01",
                        tripCode="TRIP-TEST", opening=100, unitCost=10)
            assert "TRIP-TEST" in browser.post("/setup", form)
            browser.post("/step", {"tenant": "tenant-b", "action": 1}, status=409)
            browser.post("/step", {"tenant": "tenant-a", "action": 2}, status=403)
            browser.post("/step", {"tenant": "tenant-a", "action": 1, "__RequestVerificationToken": "wrong"}, status=400)
            for action, role in enumerate(["warehouse", "sales", "sales", "sales", "sales", "cashier", "warehouse"], 1):
                browser.role(role)
                fields = {"tenant": "tenant-a", "action": action}
                first = browser.post("/step", fields)
                if action == 2:
                    assert "REJECTED_QUOTA" in first
                before = browser.report()
                page_before = re.findall(r"Bút toán ([a-f0-9-]{36})", first)
                replay = browser.post("/step", fields)
                assert "kết quả của lệnh gốc" in replay, action
                assert browser.report() == before
                assert re.findall(r"Bút toán ([a-f0-9-]{36})", replay) == page_before
            assert page_before
            assert numbers(browser.report()) == numbers(finished)
            browser.role("auditor", "tenant-b")
            assert browser.report()["checkpoints"] == []
            assert "TRIP-TEST" not in browser.get("/")
        remove(sample)
        with host("Resume", fresh) as browser:
            assert "TRIP-TEST" in browser.get("/")
            assert numbers(browser.report()) == numbers(finished)
        with host("Resume", complete) as browser:
            assert numbers(browser.report()) == numbers(finished)
            assert "AC03-4" in browser.detail("T6_RETURN", "cogs")
    finally:
        for run_id in (fresh, sample, complete):
            remove(run_id)
    print("Demo smoke passed: Fresh, AC03Start, AC03Complete, Resume, replay and isolation.")


if __name__ == "__main__":
    main()
