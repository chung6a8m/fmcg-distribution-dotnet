# APP-05.6 Implementation Plan

> **For agentic workers:** Use Superpowers test-driven development and request independent review before merging.

**Goal:** Find current follow-ups and durable APP-05 command receipts after Stop, NextDay, and Resume in a fresh browser.

**Architecture:** Begin with the immutable closure snapshot and current source evidence. Append reassignment events and use actor-scoped command history from the same transaction as the command, resolved under current tenant and Trip grants.

**Tech stack:** .NET 10, Dapper, SQL Server LocalDB, React, TypeScript, Playwright.

**Spec:** [APP-05.6](../../roadmap/app-05.md) and [daily close design](../specs/2026-11-24-dev-daily-close-design.md).

- [x] Exercise the missing history endpoint in the existing browser restart runner before implementing it.
- [x] Expose actor-owned receipts for close, assignment, successor and remainder-closure commands. Recheck both Trip scopes where execution requires both. Recover pre-upgrade successes through durable source joins.
- [x] Project actionable and finished work with current assignments, completion proof and successor links. Append reasoned reassignment using the current assignment as the concurrency token.
- [x] Verify strict D.4 report/source hash proof and cutoff boundaries.
- [x] Add the work and recovery interface to the current DEV workspace. Verify keyboard access, lost response, actor/tenant negatives, identical replay and changed payload conflict.
- [x] Run the pinned full test command, both synthetic browser/LocalDB runners, independent review and final-head CI. Record evidence in the roadmap closeout.
