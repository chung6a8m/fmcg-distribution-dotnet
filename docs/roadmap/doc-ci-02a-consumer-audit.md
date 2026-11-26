# DOC-CI-02A — Consumer audit & document boundaries

**Disposition: MERGED / FINAL_HEAD_CI_VERIFIED / MAIN_CI_VERIFIED / CLOSED.** [Closeout #169](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/169#issuecomment-7188635128), merge `5607b70d5c0d5dac6414ad7575acf213a0c7a1f4`, 10/10 workflow, 25/25 job main attempt 1. PR full attempt 2; #75 còn OPEN. Các ghi chú pending dưới đây là lịch sử lúc tạo candidate, không phải disposition hiện hành. Phạm vi SYNTHETIC_NONPRODUCTION / CI / DOCS_GOVERNANCE. Theo [roadmap DOC-CI v2](docs-pr-ci.md), không mở Light cho bất kỳ đường dẫn mới, không thay classifier/gate/routing, không mở 02B trước khi audit/review/full CI và receipt merge/main đủ.

## 1. Baseline và inventory

Baseline main `af8ecaabfd3417a258d866da7d52f01e492722fa`, tree `ade1ea17074210d8f8277770fc47a15668b29303`, merge PR [#168](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/168). Git tree API recursive không truncated: 1060 entries, 929 blobs, 245 Markdown. Đây là kiểm kê metadata, không phải đọc toàn bộ nội dung. Markdown theo nhóm: AGENTS 1, README 1, business 21, deploy 13, docs/app-01a-kickoff.md 1, closeout 27, decisions 25, evidence 1, roadmap 103, security 11, superpowers 17, ui-ux 23, web 1. Kiểm kê cả source, scripts, tools, tests, build manifests, deploy và .github; không chỉ code search (index không trả kết quả). Baseline 18,631,121 bytes blob dưới giới hạn 2 MiB. Không có thay đổi business/lock.

10 workflow tự kích hoạt PR/push main, 25 job/check theo YAML APP-00A và Jobs API push trên baseline (validate có tên ổn định; thời điểm kiểm tra APP-00A đang chạy, 9 companion đã SUCCESS). Không tuyên bố 25/25 PASS.

| Workflow file | Check/job name | Main baseline run |
| --- | --- | --- |
| app00a.yml | classify; core_sql; web_sales; app03; distribution; validate | [45785597465](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45785597465) |
| app02b2-runtime.yml | runtime-foundation | [45785600003](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45785600003) |
| app02b3-identity.yml | identity-b3; identity-sql-windows | [45785597422](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45785597422) |
| app02b4-tenants.yml | tenant-b4-unit; tenant-b4-sql | [45785599138](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45785599138) |
| app02b4-web-regressions-with-known-debt.yml | web-regressions-with-known-audit-debt | [45785597369](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45785597369) |
| app02b5-authorization.yml | b5-source; b5-two-tenant-sql | [45785597785](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45785597785) |
| app02b6-policy-governance.yml | b6-source; b6-localdb | [45785597848](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45785597848) |
| app02b7-recovery.yml | b7-source-safety; b7-localdb-restore | [45785597981](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45785597981) |
| app02b8-audit-operations.yml | b8-source-contract; b8-two-tenant-localdb | [45785597618](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45785597618) |
| security-picomatch-issue59.yml | candidate-parse-source-diff; candidate-metadata-only; full-npm-security-audit; web-glob-input-boundary; picomatch-lock-security | [45785597797](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45785597797) |

6 workflow LAB-D02/D03/D04/E05/I01 và B.9 ngoài inventory 10 workflow thường xuyên; không đổi trigger, permissions, required checks/rulesets hay runtime worker. Metadata ở đây không chứng minh branch protection.

## 2. Native Git scan và giới hạn

[Tool audit](../../tools/audit_doc_ci_consumers.py) chạy trên committed Git tree, không dùng working tree, code search hay chỉ grep literal. Ghi path, mode, blob OID, size, source commit/tree, toàn bộ Markdown matrix (kể cả v1 và candidate), reference path/line/category, generic leads và opaque inventory; dùng git ls-tree -r -l -z và git cat-file --batch. Phân biệt possible-execution, prose-link-or-authority và audit-only. Quét cả blob văn bản trong docs/tools, deploy, build manifests, src, web, tests, scripts, .github, không loại docs chỉ vì tên thư mục. Basename trùng được coi là lead mơ hồ; read/hash, glob/directory scan, import/copy/build và authority/evidence/fixture được ghi riêng. Không xuất nội dung dòng, secrets hoặc cho report làm input chọn CI. Các file binary/non-UTF8/nonregular/size limit liệt kê để reviewer không coi là đã chứng minh an toàn; lỗi Git/budget fail thay vì trả báo cáo thiếu.

Lệnh có thể chạy lại từ root checkout đủ history:

```sh
python3 -B tools/audit_doc_ci_consumers.py --ref af8ecaabfd3417a258d866da7d52f01e492722fa --output TestResults/doc-ci-consumer-audit/base.json
python3 -B tools/audit_doc_ci_consumers.py --ref HEAD --output TestResults/doc-ci-consumer-audit/checkout.json
python3 -B -m unittest discover -s tests/ci -p 'test_*.py' -v
python3 -B tools/validate_business.py
python3 -B tools/validate_e_sim_01.py
pwsh -NoProfile -File scripts/Verify-Business.ps1
git diff --check
```

Local native Git fixture 2/2 PASS: trực tiếp, đường dẫn ghép, glob, binary, dirty/untracked và ref thiếu; không phải native scan của repo thật (môi trường hiện tại không resolve github.com). Để thu evidence trong chính PR, bổ sung hai step read-only trong job classify **chỉ trên mode full**: quét event base (PR) và checkout, upload artifact app00a-doc-consumer-audit-<attempt>. Không thêm job/check, không sửa outputs/conditions của worker, classification receipt, gate verifier, command runtime, trigger, quyền hoặc companion. Light không chạy audit step mới; main vẫn full. Hai step này là phương án kỹ thuật evidence của 02A, không phải routing v2; chi phí thực tế cần ghi trong review, có thể bỏ việc thu định kỳ bằng PR full sau khi có đủ receipt. Audit thất bại không tạo PASS. Native scan baseline/candidate, kiểm các generic leads, kiểm opaque files và CI final HEAD còn PENDING. Report lexical là danh sách lead, không bảo đảm phát hiện path ghép qua nhiều dòng/dữ liệu động hoặc runtime reflection; mọi consumer chưa hiểu giữ Full. Audit-only tool không phải consumer authority.

## 3. Ma trận quyết định bảo thủ

| Path / nhóm | Consumer đã đọc ở baseline | Loại dữ liệu | 02A disposition / lý do | Regression cần giữ hoặc thêm ở 02B |
| --- | --- | --- | --- | --- |
| `business/**`, `business.lock.json` | scripts/Verify-Business.ps1, tools/validate_business.py (BR/POL/AP/AC, decisions, workflows, links) | Immutable baseline | Full, bất biến | hash/business/link/E-SIM |
| `AGENTS.md` | tools/validate_business.py L66–67 và agent instructions | Authority + link | Full; giữ toàn bộ quy tắc | instruction edit Full; links |
| `docs/roadmap/app-05.md` | agent navigation, implementation/acceptance roadmap | Hợp đồng, sequence, acceptance | Full; chỉ tách các đoạn receipt/tiến độ | #163 historical AGENTS/roadmap Full; mixed Full |
| `docs/status/app-05-status.md` | mới; AGENTS/README/roadmap navigation dự kiến, audit-only scan | Status mô tả | Candidate, **vẫn Full**; kiểm whole-tree leads trước khi cho Light | status-only Light probe 02B, mixed/unknown Full |
| `docs/receipts/app-05-receipts.md` | mới; link stub từ roadmap, audit-only scan | Receipt mô tả | Candidate, **vẫn Full**; không dùng làm evidence machine input | receipt-only Light probe 02B, mixed/unknown Full |
| `docs/roadmap/app-02c6-localdb-acceptance-matrix.md` | src/FmcgDms.Demo/App02c6Dev.cs L32, L236 | Acceptance + hash | Full | oracle C.6 |
| `docs/roadmap/app-02d0-dev-contract.md`, `app-02d1-dev-acceptance-matrix.md` cùng thư mục | App02d1Dev.cs L60–65 | Hash contract/matrix | Full | D.1 |
| `docs/roadmap/app-02d2-dev-contract.md`, `app-02d2-dev-acceptance-matrix.md` | App02d2Dev.cs L55–64 | Hash contract/matrix | Full | D.2 |
| `docs/roadmap/app-02d3-dev-contract.md`, `app-02d3-dev-acceptance-matrix.md` | App02d3Dev.cs L58–67 | Hash contract/matrix | Full | D.3 |
| `docs/roadmap/app-02d4-dev-contract.md`, `app-02d4-dev-acceptance-matrix.md` | App02d4Dev.cs L62–63, L117 | Hash contract/matrix | Full | D.4 |
| `docs/roadmap/app-02e-ev-p0/**` | tools/validate_e_sim_01.py L9, L99–101, L133 | Input oracle, Markdown/table + JSON | Full | E-SIM-01; historical freeze |
| `docs/roadmap/app-05-7-fixture.json` | App05DailyCloseEvidence.cs L20–21 | Fixture | Full | APP-05.7 |
| `docs/security/**`, `docs/decisions/**` | web/tests/picomatch-advisory.test.mjs L12–18; tools/app02b_lab/catalog.py L34 | Security/Owner authority, evidence | Full | security policy, LAB, authority |
| `docs/ui-ux/tokens/**`, `tools/**` trong UI, fonts/evidence | web/src/style.css L3–4; APP-00A workflow/toolchain | Runtime CSS, executable/build assets | Full | build, token parity, browser/security |
| `docs/roadmap/app-02.md`, C/D/E/F/LAB spec/closeout | Agent navigation, owning acceptance; còn generic leads cần disposition | Mixed authority/status | Full, không di chuyển contract | C/D/F/E, LAB guards |
| `docs/roadmap/app-03.md`, `app-03-*-*.md`, evidence ZIP | Agent navigation, owning acceptance and receipts | Mixed authority/status/evidence | Full, không move history | APP-03 suite |
| `docs/ui-ux/README.md`, adoption roadmap, decisions, verification, closeout | AGENTS guidance, UI design authority và mixed evidence | Contract/status | Full, chưa tách | UI regression |
| `docs/roadmap/app-02b-local-preprod*`, `app-02b-local-docker-wsl2*`, deploy/lab docs | AGENTS, LAB handoff, operator/acceptance authority | Mixed | Full; không thay physical/host state | LAB scope/consent/guards |
| Mọi path khác, nested docs tools, source/test/config/deploy/build manifests | Generic scan/unknown hoặc executable | Chưa chứng minh prose | Full mặc định | unknown/unlisted Full; rename/mode/blob negatives |

### 16 đường dẫn v1

Đã đọc nội dung/heading của 16 path hiện tại và classifier tại baseline. Dưới đây là đánh giá **authority** và đề xuất cho 02B; chưa kết luận không có consumer thực thi mới, cần disposition report native Git. Chính sách runtime v1 giữ nguyên.

| Exact path | Loại dữ liệu / consumer hoặc lý do | Đề xuất, chưa thực thi | Regression |
| --- | --- | --- | --- |
| `README.md` | Business/link validator đọc README; hướng dẫn/authority và lịch sử tiến độ cùng tồn tại. Chỉ xem xét giữ Light nếu kiểm lại nội dung/consumer và giới hạn phù hợp. | V1 giữ nguyên; tái audit, Full nếu còn authority | business/link; candidate-only và mixed |
| `docs/roadmap/app-00a-parallel-ci.md` | Hợp đồng phân nhóm worker, aggregate, evidence. | V1 giữ nguyên; đề nghị Full ở 02B | policy/instruction edit Full; mixed Full |
| `docs/roadmap/app-00a-build-cache.md` | Thiết kế build reuse, guard và gate. | V1 giữ nguyên; đề nghị Full ở 02B | policy/instruction edit Full; mixed Full |
| `docs/roadmap/localdb-ci-bootstrap.md` | Hợp đồng bootstrap/readiness/diagnostics, giới hạn nợ. | V1 giữ nguyên; đề nghị Full ở 02B | policy/instruction edit Full; mixed Full |
| `docs/roadmap/pr-ci-concurrency.md` | Chính sách cancel, scope và verification. | V1 giữ nguyên; đề nghị Full ở 02B | policy/instruction edit Full; mixed Full |
| `docs/roadmap/docs-pr-ci.md` | Chính sách classifier, gate, routing v1/v2; là authority CI dù v1 có allowlist. | V1 giữ nguyên; đề nghị Full ở 02B | policy/instruction edit Full; mixed Full |
| `docs/superpowers/plans/2026-11-16-issue-13-pwa-external-update.md` | Plan chỉ dẫn implementer/agent, scope và tiêu chí kiểm chứng; không coi là prose thuần. | V1 giữ nguyên; đề nghị Full ở 02B | policy/instruction edit Full; mixed Full |
| `docs/superpowers/plans/2026-11-20-ui01-runtime-foundations.md` | Plan chỉ dẫn implementer/agent, scope và tiêu chí kiểm chứng; không coi là prose thuần. | V1 giữ nguyên; đề nghị Full ở 02B | policy/instruction edit Full; mixed Full |
| `docs/superpowers/plans/2026-11-22-app-02d2.md` | Plan chỉ dẫn implementer/agent, scope và tiêu chí kiểm chứng; không coi là prose thuần. | V1 giữ nguyên; đề nghị Full ở 02B | policy/instruction edit Full; mixed Full |
| `docs/superpowers/plans/2026-11-22-app-02d3.md` | Plan chỉ dẫn implementer/agent, scope và tiêu chí kiểm chứng; không coi là prose thuần. | V1 giữ nguyên; đề nghị Full ở 02B | policy/instruction edit Full; mixed Full |
| `docs/superpowers/plans/2026-11-24-app00a-parallel-ci.md` | Plan chỉ dẫn implementer/agent, scope và tiêu chí kiểm chứng; không coi là prose thuần. | V1 giữ nguyên; đề nghị Full ở 02B | policy/instruction edit Full; mixed Full |
| `docs/superpowers/plans/2026-11-25-app00a-build-cache.md` | Plan chỉ dẫn implementer/agent, scope và tiêu chí kiểm chứng; không coi là prose thuần. | V1 giữ nguyên; đề nghị Full ở 02B | policy/instruction edit Full; mixed Full |
| `docs/superpowers/plans/2026-11-25-app03-read-diagnostics.md` | Plan chỉ dẫn implementer/agent, scope và tiêu chí kiểm chứng; không coi là prose thuần. | V1 giữ nguyên; đề nghị Full ở 02B | policy/instruction edit Full; mixed Full |
| `docs/superpowers/plans/2026-11-25-docs-pr-ci.md` | Plan chỉ dẫn implementer/agent, scope và tiêu chí kiểm chứng; không coi là prose thuần. | V1 giữ nguyên; đề nghị Full ở 02B | policy/instruction edit Full; mixed Full |
| `docs/superpowers/plans/2026-11-25-localdb-bootstrap.md` | Plan chỉ dẫn implementer/agent, scope và tiêu chí kiểm chứng; không coi là prose thuần. | V1 giữ nguyên; đề nghị Full ở 02B | policy/instruction edit Full; mixed Full |
| `docs/superpowers/plans/2026-11-25-pr-ci-concurrency.md` | Plan chỉ dẫn implementer/agent, scope và tiêu chí kiểm chứng; không coi là prose thuần. | V1 giữ nguyên; đề nghị Full ở 02B | policy/instruction edit Full; mixed Full |

## 4. Tách APP-05 và bảo toàn tài liệu

- [Status](../status/app-05-status.md): 9 trạng thái từ bảng roadmap, header lịch sử và nguyên đoạn progress AGENTS/README cũ (có snapshot cũ hơn bảng; không chỉnh thành PASS mới). Không chứa authority tự động.
- [Receipts](../receipts/app-05-receipts.md): 8 block closeout/historical, nguyên nội dung trừ link tương đối; anchor rõ. Giữ các heading/anchor gốc trong roadmap dưới dạng link stub, giữ đoạn scope/điều kiện ở các increment tương ứng và Exit .0. Các biên nhận local lẫn hợp đồng/chi tiết triển khai vẫn ở roadmap; không di chuyển toàn bộ lịch sử.
- AGENTS chỉ bỏ phần lặp progress và chuyển sang link điều hướng; giữ mọi ràng buộc, Owner authorization, nghiệp vụ, độc lập review, full final-HEAD CI và các nợ. README giữ quyết định Owner và link đến nguồn; snapshot README cũ vẫn lưu. Roadmap giữ 9 increment, thứ tự, dependencies, output, hợp đồng, acceptance/fixture, scope, checklist, exit conditions và lịch sử ở link cũ. Không sửa hồ sơ APP-02/03/UI/LAB.
- Kiểm tra cơ học 9/9 rows, 8/8 block được di chuyển với link relative rebased, 8 heading gốc giữ lại, chỉ di chuyển progress APP-05 trong AGENTS/README. Independent review đối chiếu nội dung và link/anchor trước/sau, không gán PASS tự động.

## 5. Handoff cho 02B — predecessor đã closeout

- [x] Đọc report base và checkout đúng source/tree/run/attempt; đối chiếu số tracked/Markdown/opaque, review **mọi** execution lead có thể đọc candidate/v1 (kể cả glob, hash, copy, build, imports, docs tools, link/authority). Phân loại false-positive/known consumer/unknown trong review; unknown giữ Full. Bằng chứng negative lexical không phải proof tuyệt đối. 02A đã ghi grouped/known/unknown disposition; relevant unknowns còn phải giải quyết ở 02B trước khi mở Light.
- [x] Xác nhận exact 16 paths không thay đổi, full final-HEAD 10 workflow/25 check (không tính artifact mới là check), business/link/E-SIM, native regression, receipt và worker inventory. Không gọi local fixture PASS là full CI.
- [x] Independent review nội dung, link/anchor và authority, merge, kiểm full push main trên merge SHA. Chỉ sau đó closeout 02A; nếu cần chỉnh audit/candidate, sửa cùng PR trước merge.
- [ ] 02B chỉ cho phép exact paths được review đầy đủ; nếu vẫn còn consumer hoặc authority thì không đưa vào allowlist, không thay đổi bằng chữ PASS trong hồ sơ. Đề xuất bảo thủ: hai file APP-05 mới là ứng viên duy nhất từ 02A, xét lại toàn bộ 16 v1 (đặc biệt policy/instruction plans) trong PR full 02B; 02C/02D độc lập.

Không có thay đổi business, schema, application runtime, classifier, gate verifier, permissions, tenant/physical/Owner acceptance hay CI worker steps. Không tự merge hoặc closeout theo báo cáo này.
