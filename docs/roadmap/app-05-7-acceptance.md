# APP-05.7 — Nghiệm thu ca tích hợp hai ngày

Hành trình synthetic DEV dùng một nonce và cùng hai database Windows LocalDB cho ngày nghiệp vụ 23–24/11/2026. Ngày đầu dùng profile đóng chuyến tiền nhiệm. Sau Stop và NextDay, runner giữ khóa migration của tenant đối chứng để làm gián đoạn nâng cấp rồi Resume cả hai tenant bằng manifest đã ghi. Browser context mới dùng quyền hiện hành và tìm lại biên nhận trên máy chủ.

## Chạy kiểm chứng

Từ checkout sạch trên Windows, cài .NET SDK 10.0.404, Node 26.13.1+ và Chromium. Các launcher DEV dùng cổng API chung nên chạy theo thứ tự dưới đây.

```powershell
npm --prefix web ci
npm --prefix web exec -- playwright install chromium
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifyDailyClose
pwsh -NoProfile -File ./scripts/Run-DevFunctionalRegression.ps1 -Suite All
```

Bật `APP05_DAILY_DIRECT=1` cho một lượt riêng để người thao tác đóng chuyến chính ngày đầu và chọn chuyến kế nhiệm ngày hai trên trình duyệt ngoài Playwright. Khi runner in URL, mở đúng tab DEV, dán mã DEV trong clipboard và kết nối. Ngày đầu dùng **Đóng chuyến** rồi xác nhận. Ngày hai chọn `DEV-20261124-A`, nhập 15 EA và xác nhận xếp chuyến. Runner chỉ tiếp tục sau khi máy chủ có đúng biên nhận. Sau lượt trực tiếp, xóa biến môi trường trước khi chạy chế độ tự động.

## Ma trận và nguồn đối chiếu

[Manifest fixture](app-05-7-fixture.json) ghi số lượng, giá trị và các Trip theo mã DC. Hàng chính `DAY-A-EA` có opening 60 EA và 180.000 VND, thuộc một dòng Confirm dùng cho cả hai ngày. Hàng ca sạch và ca zero-manifest lấy từ SKU riêng. Không phát sinh thu tiền trong ca này nên oracle yêu cầu custody và fund bằng 0. Ca B và F, cạnh tranh giữ hàng, release và lỗi lô được kiểm qua `VerifyASuccessor` và F09 All theo [ma trận APP-05](app-05.md#7-ma-trận-nghiệm-thu-và-truy-vết).

- Ngày đầu nạp 40, giao 25 và bàn giao 15 về MAIN dưới quyền HELD. Chuyến chính chốt với hai việc. Chuyến sạch chờ báo cáo, chuyến zero-manifest có Confirm5 và chưa Reserve, chuyến U chưa sử dụng.
- Ngày hai xếp chuyến từ đúng nguồn Confirm, chuyển HELD 15 rồi nạp và giao. Ca zero-manifest dùng Reserve mới cho 5 đơn vị. Đối soát ngày đầu hoàn tất việc D.4 theo đúng source hash. Hai nguồn giao ngày hai ở ngoài cutoff. Chuyến sạch đóng sau báo cáo, còn biên bản chốt ngày đầu giữ đúng snapshot.
- Oracle SQL chọn dữ liệu theo tenant, mã chuẩn bị và khóa ngoại của nguồn. Các ID browser nằm trong biên nhận riêng để truy vết. Bộ kiểm so sánh opening, nguồn, lô, quyền tồn, chi phí, phải thu, journal, công việc, snapshot và ảnh đối chứng.

## Artifacts và thu hồi

Runner ghi `fixture.json`, snapshot và expected/actual/diff trong `sql-review.json`, JSON checkpoint gắn ID nguồn/lệnh, JUnit, ảnh Chromium và SHA-256 từng file trong `TestResults/app05-7/<run-id>/run.json`. Sau khi host dừng, runner gọi Remove theo manifest và xác nhận cả hai database thuộc nonce đã được thu hồi. Nếu lần chạy bị ngắt giữa chừng, dùng run ID được launcher in và đúng checkout.

```powershell
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Stop -RunId <run-id>
pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action Remove -RunId <run-id>
```

GitHub chạy hành trình tự động cùng các gate hiện có. Hai thao tác trực tiếp và F09 All có biên nhận local riêng. Biên nhận thao tác trên Edge ghi hai checkpoint; ca do Owner tự thực hiện giữ trạng thái `NOT_RUN`. Phạm vi bằng chứng là synthetic DEV.

## Biên nhận local ngày 26/11/2026

| Lượt | Run ID | Chromium | SQL | Thu hồi |
| --- | --- | --- | --- | --- |
| Tự động | `47e77c6646da45fbb40fffe729ddc688` | 2/2 | 379/379, zero diff | Hai DB và manifest đã thu hồi |
| Hai thao tác trực tiếp bằng Edge | `4d9c131522ed46c8964176cd209d6eaa` | 2/2 | 385/385, zero diff | Hai DB và manifest đã thu hồi |

Hai lượt trước review ghi source `1ab4748b50b19b3d378d32f6ee872f4ef4b40ba7+working-tree`. Edge ghi đóng chuyến ngày một và xếp 15 EA vào chuyến ngày hai bằng OPS. Chromium chạy các bước còn lại. Cả hai lượt quan sát gián đoạn nâng cấp sau khi tenant A đã có schema kế nhiệm và tenant B chưa có, rồi Resume hoàn tất cả hai tenant.

- Tự động. SHA-256 `run.json` `649603FBD7FFEEA91B4BBBA1F3C1408EDA68E80F47540CE8C2B5ABC653294644`. SHA-256 `sql-review.json` `E0CFE9692D4AA5E1157C12383B5B97D4BF9E72E99C0279F2A0D5A8DC1233CAF6`.
- Edge. SHA-256 `run.json` `0CFBD217661ECD991783F76C5B26B05E3491549DF8F28A196F2EB09F3279F847`. SHA-256 `sql-review.json` `A0CFC58FDDA525EB4359A8A2188B73716188CF8BBC06F0EBA35485B0EECFC411`.

Ca Confirm5/Reserve0 làm lộ lỗi chọn kho khi tenant có nhiều kho active và chuyến chưa có quyền tồn. Bàn giao bằng 0 hiện chỉ chọn kho duy nhất có StockPool cho tất cả SKU đã Confirm trong trường hợp đó. Nếu chuyến có quyền tồn, kho vẫn được xác định từ các quyền đó. Hồi quy xác nhận bản ghi bàn giao có đúng kho và nguồn Confirm với lượng bằng 0.

Biên nhận exact HEAD cho runner tự động, F09 All và GitHub được ghi tại PR triển khai và merge commit.
