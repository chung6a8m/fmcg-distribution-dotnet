# APP-02B LAB — Codex Implementation Handoff

**Scope:** execution instructions for one planned APP-02B LAB increment at a time. The roadmap PR itself implements no runtime or acceptance result. Read [master](../app-02b-local-preprod-acceptance.md), [design](design.md) and the increment's source dependencies before editing.

**APP-02C DEV handoff (2026-11-22):** C.1–C.5 instead use the [standing DEV Owner decision](../../decisions/owner-app02c-dev-first-2026-11-22.md) and [C DEV handoff](../app-02c-dev-handoff.md): sequential source/migrations/UI and actual UI→API→SQL with dedicated synthetic data, initially within the original nonce-owned LocalDB safeguards. This B lab sequence, host capacity, Docker setup and physical `PREPROD_FOUNDATION_VERIFIED` are not blanket C development prerequisites. Keep applicable feature contracts, authority/tenant, transactions/idempotency and tests/review; retain physical gaps in [operations debt](../app-02c-operations-debt.md). The current amendment is docs-only and C implementation is `NOT_STARTED`; no B lab or physical acceptance is upgraded.

## 1. Prompt giao việc cho một PR

Copy block này và thay ID; `<ID>` là placeholder, không phải câu lệnh đã triển khai:

> Triển khai duy nhất increment LAB-XXX trong docs/roadmap/app-02b-local-preprod-acceptance.md và section spec được link. Đọc AGENTS.md, design.md, case-matrix.md và các dependency PR trước khi sửa.
>
> Tạo branch riêng từ main hiện tại, ghi base SHA. Xác minh dependency source đã merge và applicable source-integration approval. Nếu chưa merge, vẫn hoàn thành phần độc lập có thể review, rồi ghi blocker cụ thể; không cherry-pick lén PR Draft hoặc tự đóng physical issue để mở gate.
>
> Giữ phạm vi một increment: paths/interfaces/invariants trong spec; mọi đổi contract phải được giải thích và update consumer mapping. Không sửa business/, business.lock.json, frozen B9 expected fixture hoặc nới original physical oracle; giữ synthetic/LocalDB/foundation guards.
>
> Viết regression meaningful cho lỗi/negative invariant, chứng kiến FAIL trước fix rồi PASS; chạy focused project và relevant retained workflows. Không thêm tests chỉ để chứng minh câu chữ docs. Dùng same-ID retry/outcome, current authority tại protected COMMIT, actual raw witness khi thực hiện live run.
>
> Local environment phải đúng enrolled host/database/source/config/operation scope. Codex chuẩn bị concrete plan/change set trước các thao tác thay đổi máy. Không reset VM/DB không thuộc run, không lấy secret từ logs hoặc đưa secret vào argv/artifacts. Approval hiện có còn hiệu lực thì tiếp tục trong scope, không hỏi lại từng thao tác.
>
> Review diff trong context/agent riêng; xử lý findings; verify final HEAD. Cập nhật roadmap row với PR/SHA/status thực tế. Mở PR reviewable có problem/change/result, tests và remaining physical gaps; không tự merge hoặc tự đóng #53/#56 chỉ vì source tests PASS.

Một review AI khác context hữu ích cho source; không được ghi thành independent Human Security/DBA approval.

## 2. Branch, PR và ownership

Branch đề xuất: `feat/app02b-lab-<id>-<slug>`; fix CI dùng `fix/`, docs dùng `docs/`. Mỗi branch bắt đầu từ main chứa các dependencies, không có thay đổi ngoài task của người dùng. Nếu dùng worktree, tạo path riêng và không reset workspace hiện hữu.

PR title: `feat(APP-02B LAB-S04): add scoped typed lab command boundary` (đổi loại/ID phù hợp).

PR body tối thiểu:

- **Problem:** gap cụ thể và case/issue nào bị chặn.
- **Change/result:** interface, state/migration/config mới và behavior sau thay đổi.
- **Dependencies:** PR/SHA thật; source prerequisites và live prerequisites tách rõ.
- **Verification:** commands, test count/fail/skip, source SHA, CI run/attempt/artifact refs; observed FAIL→PASS nếu là fix.
- **Scope of evidence:** source/CI/local/physical đúng thực tế; môi trường/tool không có thì ghi NOT_RUN/BLOCKED.
- **Remaining gates:** các case cần raw execution, Owner decisions/custodians còn thiếu và issue tiếp tục OPEN.
- **Rollback/recovery:** source revert không đồng nghĩa DB downgrade; migration đã apply dùng forward repair, suspend trước khi thay binding/authority.

File owners: LAB-E05 owns Python/JSON provenance contract; LAB-S09 owns matching shared C# DTO, B3 fixture actors, B5 authority enrollment and versioned permission catalog; LAB-S04 owns admission/typed handlers/atomic command audit intent; LAB-S05 owns shared canonical journal and immutable identity mapping; LAB-S08 owns relay/anchor; LAB-I05 owns lifecycle orchestration and the B53 activation-permit adapter; LAB-I08 owns restore cutover extension; LAB-O01/O02 own live ops; LAB-E08..E12 own scenarios, không tạo runtime mechanism thay các owner đó. File dùng chung đổi theo PR tuần tự, tránh hai agent cùng ghi. S09 chạy trước S04 theo dependency, không theo thứ tự số ID; original ADMIN-01 physical acceptance chưa được đóng bởi fixture enrollment.

## 3. Windows tools và host placement

Đây là installation inventory cho LAB-I01, không phải kết quả máy hiện tại. Pin version/checksum/installer origin trong `toolchain.lock.json` khi triển khai; tool mới chưa có pin thì preflight báo thiếu, không cài latest âm thầm.

| Nơi cài | Công cụ | Dùng cho / ràng buộc |
|---|---|---|
| Windows | Git, GitHub CLI, PowerShell 7, editor/Codex | Repo/PR, scripts và bounded operator actions; Git credentials riêng khỏi app |
| Windows | .NET SDK `10.0.404`, SQL Server LocalDB | Existing Windows synthetic regressions; LocalDB không thay full SQL PREPROD |
| Windows | Node `>=26.13.1 <27`, npm, locked Playwright browsers | Existing web/CI audits and browser/PWA regression; không publish frontend bằng #59 exception |
| Windows observer | Python, OpenSSL, SSH client | Schema/collector/driver, evidence signatures, guest management; private keys không vào repo |
| Windows | Hyper-V theo edition/CPU eligibility | APP/DATA/VAULT VM isolation; 32/64 GB profiles chỉ là estimates |
| Linux APP | Docker Engine/Compose, Caddy, runtime images | Foundation image giữ nguyên; riêng Lab.Api/Lab.Worker từ S04/S06 |
| Linux DATA | Pinned supported SQL Server package/image | Same instance for protected cross-DB transaction, TLS, memory cap, native backup |
| Linux VAULT | Persistent Vault + audit/TLS | Scoped secrets; normal Raft mode; recovery material tách APP/DATA |
| Windows/OBSERVER | OpenTelemetry Collector, Seq, outcome/alert journals | Raw traces/receipt history outside APP/DATA rollback |
| Theo nhu cầu driver | Pinned network/fault utilities | Chỉ thêm khi có named fault, allowlist/deadline/cleanup; không mặc định cần Kubernetes/broker |

Nếu laptop không phù hợp Hyper-V/full SQL, vẫn viết và chạy source/validator tests; dùng approved remote lab hoặc fallback WSL2 cho rehearsal có nhãn đúng. Không giả định sandbox/cloud của Codex đang là laptop Windows hoặc có quyền quản trị máy người dùng.

## 4. Command registry và verification tiers

Các lệnh dưới đây chạy từ repo root, trừ phần web có working directory riêng. Đây là hướng dẫn cho implementation PR tương lai; roadmap docs PR chưa chạy chúng. Pinned SDK/AGENTS và workflow source là authoritative. [Microsoft MTP command reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test-mtp) mô tả `--project`/`--solution`; đừng đổi sang VSTest options theo trí nhớ.

### 4.1 Read-only baseline

```powershell
git status --short
git rev-parse HEAD
dotnet --version
node --version
python --version
openssl version
```

Xác minh business snapshot bằng verifier đã có trong APP00A workflow và lock; không tạo lại lock để hết diff. Record actual working tree/source, không sửa identity một run sau collection.

### 4.2 Existing .NET suites

Focused invocation mẫu:

```powershell
dotnet test --project tests/FmcgDms.Preprod.Authorization.Tests/FmcgDms.Preprod.Authorization.Tests.csproj -c Release -- --report-trx
```

| Increment/group | Existing projects tối thiểu cần xem/chạy khi ảnh hưởng |
|---|---|
| E02–E04, S05–S07 shared kernel | `tests/FmcgDms.Api.Tests/FmcgDms.Api.Tests.csproj`; toàn bộ APP00A workflow |
| I02/I03, S04/S06 host/admission | `tests/FmcgDms.Preprod.Runtime.Tests/FmcgDms.Preprod.Runtime.Tests.csproj`; B2 container/ingress/Vault checks |
| S01–S03, S09 enrollment, S04 session guard | `tests/FmcgDms.Preprod.Identity.Tests/FmcgDms.Preprod.Identity.Tests.csproj` và `tests/FmcgDms.Preprod.Identity.SqlServer.Tests/FmcgDms.Preprod.Identity.SqlServer.Tests.csproj` |
| S01/S04/S06/S07/S09, I05 permit, I08 | `tests/FmcgDms.Preprod.Authorization.Tests/FmcgDms.Preprod.Authorization.Tests.csproj` và `tests/FmcgDms.Preprod.Authorization.SqlServer.Tests/FmcgDms.Preprod.Authorization.SqlServer.Tests.csproj` |
| I05/I08, S04 | `tests/FmcgDms.Preprod.Tenancy.Tests/FmcgDms.Preprod.Tenancy.Tests.csproj` và `tests/FmcgDms.Preprod.Tenancy.SqlServer.Tests/FmcgDms.Preprod.Tenancy.SqlServer.Tests.csproj` |
| S07 | `tests/FmcgDms.Preprod.Policy.Tests/FmcgDms.Preprod.Policy.Tests.csproj` và `tests/FmcgDms.Preprod.Policy.SqlServer.Tests/FmcgDms.Preprod.Policy.SqlServer.Tests.csproj` |
| I06–I08 | `tests/FmcgDms.Preprod.Recovery.Tests/FmcgDms.Preprod.Recovery.Tests.csproj` |
| S08, O01/O02 | `tests/FmcgDms.Preprod.Operations.Tests/FmcgDms.Preprod.Operations.Tests.csproj` và `tests/FmcgDms.Preprod.Operations.SqlServer.Tests/FmcgDms.Preprod.Operations.SqlServer.Tests.csproj` |

Full legacy solution command currently retains 296 minimum, not a future fixed total:

```powershell
dotnet test --solution FmcgDms.slnx -c Release --minimum-expected-tests 296 -- --report-trx
```

Solution does **not** cover every B project. LAB-E07 builds a versioned command registry from the actual workflows/projects; it must fail if a required project/check disappears. New tests raise verified count as appropriate. Run every relevant dedicated project explicitly; retained full workflows remain merge gates.

### 4.3 Proposed new tests

After owner increments create these projects/modules:

```powershell
dotnet test --project tests/FmcgDms.Preprod.OperatorTool.Tests/FmcgDms.Preprod.OperatorTool.Tests.csproj -c Release -- --report-trx
dotnet test --project tests/FmcgDms.Preprod.Lab.SqlServer.Tests/FmcgDms.Preprod.Lab.SqlServer.Tests.csproj -c Release -- --report-trx
python -m unittest discover -s tests/app02b_lab -p "test_*.py" -v
```

Full-SQL tests require an explicitly allowed disposable/enrolled instance, run marker and exact DB allowlist. No silent skip when that suite is required; unavailable setup is BLOCKED/failed gate, not a green empty suite. CI test DBs use disposable roles and honest CI classification; local operator evidence uses separately authorized identities. Tests must not loosen existing LocalDB guards to connect full SQL.

### 4.4 Existing B9 validator

```powershell
python -m unittest discover -s tests/app02b9 -p "test_*.py" -v
```

Baseline is 12 tests, 11 mutation negatives; confirm current reviewed workflow before claiming counts. Missing physical bundle should return NO_GO/exit 2, 30 NOT_RUN and all five NOT_VERIFIED. A source CI job asserts this rejection as correct behavior; it does not mark the physical gate PASS.

### 4.5 Web and security stages

Working directory `web/`; use existing locked dependencies and do not change dependency graph as part of an unrelated increment:

```powershell
npm ci
npm test
npm run test:dependencies
npm run build
npm run test:pwa-build
npm audit --audit-level=moderate
npm audit --omit=dev --audit-level=low
npm run test:e2e
```

Some stages require the workflow's API/web server orchestration and pinned Playwright browser install. Inspect `.github/workflows/app00a.yml` and B4 web workflow; the list above is not a replacement for their full job. #59 policy evaluation must consume and report original audit exits/findings; do not turn an expected nonzero audit into a patched/PASS claim or bypass the audit stage. Never add `npm audit fix --force` to “complete” this roadmap.

### 4.6 Lab orchestration after implementation

Proposed commands, implemented by the assigned increments:

```powershell
pwsh scripts/lab/Test-LabPrerequisites.ps1 -EnvironmentFile <approved-config-path> -OutputDirectory <protected-output-path>
pwsh scripts/lab/New-LabVirtualMachines.ps1 -EnvironmentFile <approved-config-path> -Plan
python tools/app02b_lab/run.py --config <public-config-path> --selection case:B01-01 --run-id <run-id> --evidence-root <protected-outside-repo>
python tools/app02b_lab/run.py --config <public-config-path> --selection gate:G-B03 --run-id <run-id> --evidence-root <protected-outside-repo>
python tools/app02b_lab/run.py --config <public-config-path> --selection all --run-id <run-id> --evidence-root <protected-outside-repo>
```

Use the same immutable run only when all lineage constraints match; a new source/config/fixture starts a new run. Driver attempts are append-only. Exit 0 is selected technical requirements complete, exit 1 FAIL, exit 2 BLOCKED/NOT_RUN. `all` in a solo profile is expected to remain blocked for original independent-review requirements; do not suppress that exit.

## 5. Evidence layout and source freeze

Recommended protected evidence layout, owned by E05; no raw/private evidence is committed:

```text
<evidence-root>/<run-id>/
  run.json
  enrollment-references.json
  attempts/<attempt-id>/cases/<case-id>/raw/
  attempts/<attempt-id>/cases/<case-id>/witnesses/
  runtime/
  ci/
  hashes/
  report.json
  report.md
```

Paths are examples, not authority. Enforce root boundary, no symlink escape, canonical serialization, duplicate-property rejection, raw-before-descriptor write, complete checksums and restricted file ACL. Public report redacts personal/private infrastructure identifiers while preserving correlation to protected originals.

**Final source protocol:**

1. Finish all intended source/docs changes and update roadmap state before freezing candidate H. Build images once from clean H and bind source/config/fixture/migration/image digests.
2. Run required CI on H (with explicit source/workflow lineage where a PR merge ref exists), then perform actual authorized scenarios. Sign/retain original raw evidence outside Git; signatures are made only by the actual custodians.
3. New source, config, fixture or image provenance invalidates the affected run/review. Do not edit manifests to make older evidence look current.
4. A later docs-only commit D may refer to H's historical results. It must not say D was deployed or accepted. No recursive “commit the current SHA, changing that SHA again” loop; original B05-06 remains intact.
5. E13 produces the truthful local report and physical handoff index. Original oracle uses externally approved roots outside repo/bundle; local keys/class cannot be promoted into physical custody.

### 5.1 Increment closeout

Use one existing spec/closeout section per increment as the canonical record. Apply this convention to future updates; retain the original #91/#92 receipts and all failed attempts as history.

- Update the increment's roadmap row and its canonical section in the implementation PR: scope, dependency PRs, observed verification, remaining live requirements and current source state. Before merge, use SOURCE_REVIEW; set SOURCE_MERGED only after GitHub confirms the merge.
- Record the exact final source H, CI run/attempt/artifact references, reviewer disposition and later merge commit M in the originating PR metadata. Cite the source/merge receipts when the next substantive documentation update touches that increment. A post-merge documentation correction may have its own PR when it fixes a real gap; a PR whose only purpose is acknowledging the previous docs PR's merge is unnecessary.
- A documentation commit D may reference verified H and M. It cannot be its own pre-known final SHA and cannot inherit H's runtime acceptance. Keep B05-06 source/review correlation and revalidation intact; never add a tree-equivalence waiver to the physical oracle.
- Keep source merge, CI scope, actual operator-host/lab observations and original physical acceptance in separate fields. LOCAL_OPERATOR_LAB / SOLO_OWNER_SELF_ATTESTED does not establish independent human review, off-host custody or PREPROD_FOUNDATION_VERIFIED. Preserve FAIL, BLOCKED, NOT_RUN and NOT_VERIFIED with their evidence/reasons.
- When an older documentation PR is superseded, compare its diff with the merged canonical record, retain useful exact-source reviews/artifact hashes and failed CI history, then close it without merge under the authorized scope. Record the successor PR and observed closure; preserve its historical source rather than copying stale states over main. PR #73 is the [recorded example](../../closeout/2026-11-18-app-02b9-source-only.md#pr-73-predecessor-disposition).
- Each handoff names the next real dependency or evidence gap. Current sequence for this B lab track: rerun I01 on the intended laptop and resolve reported FAIL/UNKNOWN; implement guarded I02; then I03–I05 for LOCAL_LAB_READY. E03/E04 investigate #75 independently. An unavailable host/approval stays a named live prerequisite while independent source work proceeds.

## 6. Review checklist

- Does the PR consume a real implemented dependency, or reference an interface which is still only prose?
- Does caller input ever become tenant/actor/MakerId/epoch/connection authority?
- Are authorization, source verification, effect and audit intent atomic on the same transaction?
- Are remote effects treated with outcome identity rather than impossible distributed atomicity?
- Can restore, expiry, revoke, lost ACK, restart or partial filesystem failure reopen privilege or hide UNKNOWN?
- Does every PASS point to original raw artifacts, exact source/target, expected negative invariant and honest custody?
- Are solo/CI/source results clearly separated from original physical acceptance?
- Do status, commands and issue references match the final reviewed revision?

Resolve blockers before requesting merge. Missing tool/environment/reviewer is a specific outstanding condition, never a fabricated success. One independent PR can finish its source scope while dependent operational evidence remains explicitly unverified.
