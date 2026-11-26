# APP-05.7 integrated daily close implementation plan

> Use test-driven development, independent review and final-HEAD verification.

**Goal:** Run one daily-closing journey across two business dates on one nonce-owned LocalDB pair.

**Architecture:** Start the trip-closure predecessor, drive day one via Chromium, stop and advance the clock, interrupt the successor upgrade at the second tenant, resume, and drive day two in a fresh browser. SQL selects proof from named fixture and authoritative sources. Browser responses provide trace pointers.

**Stack:** .NET 10, Dapper, Windows LocalDB, Playwright Chromium and the existing React workspaces.

**Spec:** [APP-05.7](../../roadmap/app-05.md#app-057--nghiệm-thu-ca-tích-hợp) and [fixture manifest](../../roadmap/app-05-7-fixture.json).

- [x] Publish fixture quantities and expected branches before running the acceptance suite.
- [x] Verify the missing launcher action fails, then add the guarded launcher, date transition, interrupted upgrade and cleanup.
- [x] Drive day-one closure and next-day recovery, reconciliation and successor fulfillment with real UI/API calls.
- [x] Capture SQL independently before, after day one, after upgrade and after day two. Compare stock, cost, source IDs, rights, reconciliation, preserved history and tenant isolation.
- [ ] Run the browser check, direct browser checkpoint option, F09 All and final gates. Preserve exact source/tree, independent review, final HEAD and evidence hashes.
