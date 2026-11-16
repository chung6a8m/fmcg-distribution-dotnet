# OWN-APP00B-13-03 — 100% browser and PWA acceptance

**Decision:** ACCEPTED by the repository Owner, **2026-11-16**.

## Evidence accepted

- Actual Microsoft Edge 157 on Windows 11 at 100% zoom: desktop 1280×800 and narrow 375×812 CSS viewports, with readable compact layout and no unintended horizontal overflow.
- Actual production PWA V1 → V2 lifecycle at the same origin: a visible **Có phiên bản mới** prompt, no silent reload before consent, visible keyboard focus on **Cập nhật ứng dụng**, and explicit activation/reload to the V2 marker.
- Node 24.22.2 / npm 11.20.0 production build evidence, the existing Playwright 100% UI/PWA regression suite, .NET/LocalDB verification, and final-HEAD CI evidence recorded in [PR #19](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/19).
- Durable steps, metrics and screenshots in [Issue #13 evidence](../evidence/issue-13/2026-11-16/README.md), cross-linked from [Issue #13](https://github.com/chung6a8m/fmcg-distribution-dotnet/issues/13#issuecomment-6903980759) and [PR #5](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/5#issuecomment-6903981169).

## Disposition

- The APP-00B/C 100% browser/PWA exit gate is **ACCEPTED**. Issue #13 is authorized to close after this record lands on `main` with green final-HEAD CI.
- APP-01A is no longer blocked by APP-00B/C browser acceptance and may begin as a separate, reviewed increment under its own acceptance criteria.
- The 125% browser zoom / Windows scaling decision remains **EXCEPTION_ACCEPTED / NOT_TESTED** under [OWN-APP00B-13-02](owner-app00b-13-02-125-scale-exception.md); this decision does not convert it to PASS or deferred mandatory work.
- No production application code change was required for this acceptance record.

## Boundary

This decision applies only to the APP-00B/C synthetic/nonproduction frontend exit gate. It is not EV/P0 acceptance, accessibility certification, offline business-write authorization, production approval, pilot approval or tenant go-live authorization.
