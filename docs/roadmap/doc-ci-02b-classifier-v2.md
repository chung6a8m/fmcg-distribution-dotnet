# DOC-CI-02B — Status/receipt và classifier v2

**Disposition: SOURCE_CANDIDATE / REVIEW_AND_CI_PENDING.** SYNTHETIC_NONPRODUCTION / CI / DOCS_GOVERNANCE. Owner yêu cầu 26/11/2026; base `5607b70d5c0d5dac6414ad7575acf213a0c7a1f4`, tree `e22061232d8fec725d73e45b3707ba4271f1bbff`. [02A closeout](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/169#issuecomment-7188635128) full main 10/10 workflows, 25/25 jobs attempt 1; PR attempt 2 giữ failure history #75. Không mở 02C/02D, không claim runtime/PREPROD/EV/P0/Pilot.

## Exact candidate policy

| Path | Disposition và lý do |
| --- | --- |
| `docs/status/app-05-status.md` | Candidate Light chỉ modification regular blob; descriptive index, snapshot/authority link only |
| `docs/receipts/app-05-receipts.md` | Candidate Light chỉ modification regular blob; historical receipt, non-authoritative append |
| 16 v1 paths | Full: README mixed authority; CI policy và plans là chỉ dẫn/contract. Không wildcard |
| AGENTS, APP-05 roadmap, business/lock, decisions, security, fixtures, src, tests, tools, workflow, mọi path khác | Full |

Đọc artifact main [15104235543](https://github.com/chung6a8m/fmcg-distribution-dotnet/actions/runs/45792877006/artifacts/15104235543), SHA256 `9b239ede78f97e2cfc52634584cb6ee017639152c12993e91a79d372edd4caf5`, đúng commit/tree 934 tracked, 909 text, 25 opaque. Status có 7 prose/navigation + 5 audit-only; receipt 14 prose/navigation + 1 audit-only; 0 direct possible-execution. [02A review](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/169#pullrequestreview-6071598389) ghi 462 generic leads grouped/known/unknown, không chứng minh tuyệt đối. Business validator đọc README/AGENTS/business và tồn tại link, không nội dung target; C/D/E-SIM, LAB, security, APP-05 fixture và UI token inputs là path riêng, giữ Full. Vite config dùng Tailwind, CI build từ web, CSS import hai file token explicit; reference compiler source(none), không dùng status/receipt làm input trực tiếp. Tailwind implicit discovery và các generic/dynamic/opaque leads còn cần reviewer xác nhận relevant scope (build parity nếu cần); không coi zero direct ref là đủ. **Hai path ở candidate chưa được cấp Light trên main. Không merge nếu audit còn relevant unknown; thu hẹp về Full nếu không chứng minh được.** Historical excerpts là snapshot, không phải authority; reviewer xác nhận boundary. Không đổi app/runtime để đạt Light.

## Contract chung bàn giao 02C (candidate)

`tools/classify_app00a_pr.py`: 2 exact names, case-sensitive, chỉ M 100644→100644; add/delete/rename/type/mode/unknown Full. Whole PR từ merge-base và integration event-base→tested merge, ordered parents, nonshallow checkout, raw/NUL, full old/new blob hash verify, không policy self-exemption theo path. Defense-in-depth sau review: exact allowlist còn phải thỏa namespace phẳng `docs/status/` hoặc `docs/receipts/`, tên Markdown thường (không instruction `agents.md`); không phải wildcard cấp Light. Old/new blob đều phải UTF-8 không NUL, nếu không Full (`prose_blob_not_text`). Regression native Git sửa control file đã tồn tại mode 100644, cố ý thêm path vào allowlist vẫn Full; gate cũng từ chối receipt Light giả tương ứng. Push luôn Full. Receipt v2: giữ identity fields và changes proof v1, thêm diagnostics {phase, offending_count, offenders}. Phases event/context/whole-pr/integration/comparisons/blob-verification/complete; offender preview lossy ASCII max 240, max 8 comparison records, tổng count; log/summary không in raw blob, escape control/Markdown/workflow syntax. Full changes rỗng, docs-only diagnostics empty/complete. Version 1 historical chỉ đọc evidence, gate v2 từ chối v1/malformed/duplicate key. Diagnostics không phải proof để skip. Exact policy/worker expected states: Full success ×4, Light skipped ×4; classify success và source match. Cơ chế không chống được người có quyền sửa cả workflow/gate/test để tự báo PASS; cần independent review và branch protection, không cấp quyền bypass.

Audit report schema 2 ghi active_allowlist thay v1_allowlist, candidate inventory không tự cấp route. Không thay worker, triggers, validate name, permissions, companion hoặc business files.

## Verification và merge gate

- Native Git existing regression giữ identity/history/blob/mixed/integration; thêm 16 old authority paths và policy edits Full, both candidates Light, A/D Full, bounded diagnostics/control-path escape, v2 schema rejection và worker truth tables. Chạy `python3 -B -m unittest discover -s tests/ci -p 'test_*.py' -v` tại root.
- PR implementation Full, cả 10 workflows/25 jobs, source-bound audit/receipt và full evidence. No local clone: sandbox DNS không resolve github.com; GitHub hosted execution cần thiết. Review audit and gate schema trước merge.
- Frozen draft probe: base candidate branch, head chỉ sửa status/receipt, từng probe ghi SHA/base/integration/run/attempt, classify docs-only, 4 workers skipped, validate success và 9 companion unchanged. Close probe không merge. Probe là route proof, không runtime PASS. Nếu code đổi thì chạy lại trên HEAD mới. Main full sau merge; closeout only after main. Không mở 02C trước khi có đầy đủ receipts.
