# APP-02B.3 — PREPROD Identity and Session Foundation (nonbusiness)

**Implementation scope:** OWN-APP02B-03 (approved), in a separate code PR. B.2 was merged only as `EPHEMERAL_CI_RUNTIME_TESTED`; **real PREPROD RUNTIME-01/02, G-B01..05, P0, actual AC-11/EV-08 remain NOT_VERIFIED**. This increment never authorizes tenant SQL routing, business operations, privileged Control Plane, production/Pilot, or APP-02C/D/E.

## Boundary and API contract

- Independent .NET 10 `FmcgDms.Preprod.Identity` with `IdentityCore<PreprodUser>` and SQL-backed EF Core `IdentityDbContext`. Identity users are **disabled by default**, operator-created via separately approved administrative provisioning (B.4). No public sign-up or tenant/member grant.
- `POST /auth/login` with username/password, `POST /auth/refresh` using only `__Host-fmcg-preprod-refresh`, `POST /auth/logout` and `POST /auth/revoke-all` with a validated access bearer, `GET /auth/me` for actor/session only. Strict cookie host-only, HttpOnly, Secure, SameSite=Strict, Path=/; exact same-origin HTTPS Origin mandatory for every state-changing request. All token responses `Cache-Control: no-store`.
- Access JWT: 5 minutes, HS256 (current implementation technical choice, **not** Owner-prescribed), strict issuer/audience/lifetime/key validation, zero clock skew, `sub` + `sid` + `jti` + `session_epoch` only. No tenant/role/permission claims. Every bearer request revalidates session, user disabled/lockout/security stamp and epoch in authoritative control-plane SQL. Database outage denies the request.
- Refresh: 32 CSPRNG bytes base64url, SHA256 hex digest only at rest; 7-day technical TTL. Per-session family; rotation consumes the old grant and inserts successor in one Serializable SQL transaction. Reuse of a consumed grant commits session revocation+epoch bump **before** denial. Logout, revoke-all, account disable and security stamp change invalidate current access. No token values in logs or metrics.
- OIDC-ready stable `PreprodUser.Id`, `ExternalIssuer`/`ExternalSubject` unique mapping fields. No OIDC provider selected and no external subject auto-provisioning. `System Administrator` is not a tenant business principal.
- Key and SQL connection material are retrieved at startup **only from Vault KV-v2** `preprod/data/identity/api` using the API workload token. Worker has no read permission. Neither key nor password appears in Compose environment or source; lack of Vault/key/schema/SQL stops activation.
- B.2 API and Worker remain `FOUNDATION_ONLY / NO_GO`. Identity endpoints are opt-in via **nonsecret** `PREPROD_IDENTITY_ENABLED=true` plus `Identity:Scope=AUTH_ONLY`; if false, routes remain 404. The Caddy allowlist adds only five `/auth/*` routes, never business/admin/synthetic. No automatic schema creation at API boot.

## Required dependencies and honest limitations

1. B.3 EF Core control-plane schema is defined but **must be provisioned through a versioned, reviewed, idempotent migration/approval path before a real PREPROD enablement**. B.4 implements Tenant Registry/provisioning; B.3 must not create an unapproved Control Plane database automatically. This PR tests the schema with disposable SQLite/EF `EnsureCreated` **only in CI**; this is not a SQL Server migration or physical PREPROD witness.
2. B.5 adds authoritative tenant membership/permissions and **external monotonically fenced revocation epoch**; B.7 adds safe restore of the fence. The local session epoch prevents ordinary reuse but is **not protected from control-plane snapshot rollback**. A restored session snapshot cannot be treated as authorized production/PREPROD business access.
3. This PR implements session handling; B.4/B.5 must attach a separately revalidated tenant authority before any tenant action. No seed default admin, no password reset, MFA, user provisioning UI, cross-device session listing or external OIDC login is claimed.
4. Key rotation is fail-closed: this release accepts only the configured current `kid` and key. Removing the old key immediately invalidates existing access JWTs; the active refresh session can obtain a new one after new key activation. Multi-key grace/revocation key registry and independent key governance require B.5/B.8 review.
5. There are no operator-owned PREPROD/VPS/physical SQL gates in GitHub-hosted CI. The maximum report is **B3_CI_TESTED / REAL_PREPROD_IDENTITY_NOT_VERIFIED**. Do not claim `IDENTITY_VERIFIED` on SQLite tests alone; original 30 B.1 cases and G-B01..05 remain NOT_RUN.

## CI acceptance matrix (this increment)

| Case | Test witness | Status before final-HEAD CI |
|---|---|---|
| CI-B3-01 | Identity password hash, disabled user, invalid credentials, no plaintext stored | PENDING |
| CI-B3-02 | Signed issuer/audience/key/expiry/jti/sid validation, no tenant grants | PENDING |
| CI-B3-03 | 256-bit opaque token, hash at rest, one-time atomic rotation and replay family revoke | PENDING |
| CI-B3-04 | Logout, revoke-all, security-stamp changes, actor/session mixing denied | PENDING |
| CI-B3-05 | SameSite/Secure/HttpOnly host-only cookie, HTTPS Origin/CSRF negatives | PENDING |
| CI-B3-06 | Vault-only secret bootstrap, missing/unscoped secret fail-closed, no hardcoded key | PENDING |
| CI-B3-07 | B.2 inert/no-go, worker secret isolation, business/admin/synthetic routes stay blocked | PENDING |
| CI-B3-08 | B.2 Linux/Windows and APP-01E.5/APP-02A.4 regression, exact final HEAD and independent review | PENDING |

**Merge discipline:** separate PR, final-HEAD CI, independent review, 0 unresolved blockers. Owner has not approved a B.3 CI-only alternative to the original real PREPROD verification; therefore code integration (if reviewed) must never be labeled `IDENTITY_VERIFIED` or `PREPROD_FOUNDATION_VERIFIED` without the required real environment evidence.
