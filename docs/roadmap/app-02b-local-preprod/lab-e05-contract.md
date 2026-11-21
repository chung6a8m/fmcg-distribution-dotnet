# LAB-E05 local evidence contract

**Status** | **Scope**
--- | ---
SOURCE_REVIEW | Python/JSON contract, portable runner and disposable source tests
LAB_EXECUTED | No authorized lab measurements recorded here
Original gates | NOT_VERIFIED

## Operator inputs

Use a clean checkout and an existing, operator-protected local evidence directory outside
all Git worktrees. Restrict the evidence directory and input directory to the operator
and authorized custodians. On Windows use a local drive with restricted NTFS ACLs. Keep
the authorization record and raw host fingerprint under their designated custody. The
runner checks pins and recorded admission. LAB-I01 owns measuring platform state and
collecting the approved inputs. A file supplied by the operator does not prove that
a live host or SQL binding has been independently measured or that execution was approved.

Keep these three inputs together outside the repository and evidence root.

| Input | Contents |
| --- | --- |
| Public configuration passed as --config | APP02B-LAB-ENVIRONMENT-V1. Includes environmentId, hostSha256, authorizationRecord, secretReferences, and targets.A/B. |
| host-fingerprint.bin | Protected original bytes of the measured host fingerprint. The public hostSha256 must match those exact, nonempty bytes. |
| lab-admission.json | Protected APP02B-LAB-ADMISSION-V1 with environmentId, hostSha256, current targets.A/B and evidenceRoot, the canonical absolute path approved for writing. |

Use a redacted environment ID with the format lab-<slug>. Use opaque reference IDs of
the form ref-<16–64 lowercase hex digits>; references identify records, never credential
values. The authorization record has reference, version and sha256. The hash pins the
externally retained authorization record. Each secret reference has reference and version.
Each target contains targetRef, identitySha256 and registryVersion. A/B must have distinct
references and identity hashes. Obtain current admission separately from the public
manifest. Passwords, credentials, connection strings, raw tenant data and private addresses
do not belong in the public manifest or command arguments.

The B.9 fixture pin is
9d7f89e4ccc941ba2a879509f32f7f4e74ca1b4fb5aad748e9a7e7851e34df81.
An existing Windows checkout can retain CRLF bytes after taking the Git attribute update.
Use a fresh checkout if Get-FileHash reports a different digest and the committed blob
is unchanged.

## CLI

From the checkout root in PowerShell 7, after supplying approved inputs:

```powershell
$run = 'b-lab-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N')
$config = 'C:\Protected Lab Inputs\public-config.json'
$evidence = 'C:\Protected Lab Evidence'

python tools/app02b_lab/run.py --config $config --selection case:B01-01 --run-id $run --evidence-root $evidence
.\scripts\lab\Invoke-LabAcceptance.ps1 -Config $config -RunId $run -EvidenceRoot $evidence -Selection gate:G-B05 -Resume
.\scripts\lab\Invoke-LabAcceptance.ps1 -RunId $run -EvidenceRoot $evidence -Validate
```

Selections are case:<original case ID>, gate:G-B01 through gate:G-B05, all (30 cases
and both probes) or runtime:RUNTIME-01 / runtime:RUNTIME-02. Prepare refuses an existing
run ID. Resume appends an attempt. Validate is read-only, including for an archived bundle
outside the checkout. It checks the retained source identity without requiring an archive
to match today's HEAD. Resume always verifies the current checkout.

| Exit | Meaning |
| --- | --- |
| 0 | All selected technical requirements completed. Reserved by LAB-E05 for future reviewed measurement assessors. |
| 1 | Technical FAIL, bundle integrity failure or invalid input. |
| 2 | BLOCKED or NOT_RUN. Normal LAB-E05 result with valid inputs and absent drivers. |

CLI JSON includes the validation result, selected outcomes, prior attempt IDs and a checkpoint
when the bundle is valid. Validation success describes bundle integrity. The baseline Markdown
and JSON describe prepared coverage. B05-02 is BLOCKED in the solo profile. Multiple accounts,
keys, or AI review do not establish independent human review.

## Serialization and run lifecycle

The public interfaces are create_context(config_path, evidence_root, source_sha, run_id),
load_catalog(fixture) and validate_lab_bundle(root, *, checkpoint=None).
create_context checks inputs and returns a frozen value. The bundle layer publishes it
using exclusive run reservation. The three schemas use JSON Schema Draft 2020-12. JSON
produced by the contract is UTF-8, ASCII escaped, sorted by key, compact and LF-terminated.
Timestamps use UTC with seconds precision and a Z suffix. Digests use lowercase hex.
Configuration hashes cover canonical public JSON. Fixture and host digests cover original
bytes. The parser rejects duplicate properties, invalid Unicode and nonfinite numbers.

The runner stages the full PREPARED bundle, including run.json, the frozen fixture,
environment.json, protected originals, enrollment references, and the immutable report.json
and report.md. Coverage includes 30 original cases and two separate runtime rows. A
prepared baseline is visible before the runner reserves its first attempt. The runner
retains numbered hashes/ journal entries independently of attempts/ directories. Each
entry binds its attempt bytes and the preceding entry hash. Write-once publication, checksums,
relative artifact inventories and journal validation preserve attempt history. A crash after
attempt reservation leaves the immutable baseline and prior attempts available. Validation
reports an integrity failure if the reservation lacks the expected attempt. Preserve that
run for investigation and create a new run ID to continue. A crash during initial staging
can leave a preparation reservation outside the canonical run path.

Keep valid checkpoints from independently retained storage outside the evidence tree.
The checkpoint consists of runId, baselineSha256, attemptCount and journalHeadSha256.
Pass the retained JSON object using --checkpoint with --validate or --resume (PowerShell
-Checkpoint). A checkpoint describes the exact archived/pre-resume bundle snapshot. After
an authorized append, retain the new checkpoint alongside the earlier one. Bundle-only
validation detects broken inventories and hashes. An independently retained checkpoint is
required to detect a coordinated rewrite of those hashes or deletion of the entire journal
tail together with its attempts.

Source, environment, configuration, fixture, host and admitted target baseline must remain
fixed throughout a run. Old→new LAB-I08 lineage receipts have a typed V1 shape with
tenant, old/new binding and external approval reference. LAB-E05 refuses all target drift.
LAB-I08 owns verifying admission of a restore transition and Registry update while
retaining the original target identity.

Evidence uses closed schemas for results and witness descriptors. Attempts bind source,
configuration, environment, scope and target references. Witnesses bind run, attempt, case,
target, phase and fault and reference separately hashed bytes in the same attempt. The
validator checks witness paths, source, hash, nonempty raw data and required kinds.
Supporting measurement interpretation awaits reviewed collectors and drivers. LAB-E05
rejects all technical PASS candidates, including structurally complete self-assertions.
Every local result keeps acceptanceStatus=NOT_VERIFIED. Runtime/CI folders start empty.

Path validation rejects traversal, links, reparse points and linked evidence files.
File publication refuses an existing destination. Windows holds directory handles against
concurrent replacement during writes. POSIX uses owner/permission checks and write-once
publication with directory sync. Keep the evidence and its ancestry under trusted operator
custody. The contract does not protect against a privileged or same-account adversary who
can rewrite both evidence and its separately retained checkpoint. Never commit raw or
private evidence.

## Consumers

| Increment | Contract handoff |
| --- | --- |
| LAB-I01 | Provide measured host bytes, externally authorized redacted environment and protected admission. Check toolchain and local root prerequisites. |
| LAB-E06 | Implement typed collectors and bind raw-before-descriptor evidence to the V1 attempt/case/phase/target. Introduce reviewed interpretation of raw measurements before enabling technical PASS. |
| LAB-E07 | Populate source-bound CI evidence through a reviewed inventory extension. Current CI directory stays empty. |
| LAB-S09 | Match the shared C# DTO to the eight run-context fields and deterministic JSON contract. Keep enrollment identities and approvals external. |
| LAB-I08 | Verify approved lineage receipts and Registry transitions before admitting a changed target. |
| LAB-E08–E12 | Use the frozen case definitions, append attempts and preserve positive/negative runtime evidence and failures. |
| LAB-E13 | Build a current report from validated coverage and all attempts. Retain original independent physical B.9 requirements. |

Source review and CI are separate from local measurements and physical acceptance.
SOURCE_MERGED requires an actual merge. LAB_EXECUTED requires authorized and observed
measurements. This contract leaves original physical acceptance NOT_VERIFIED.
