# LAB-I01 Windows preflight

LAB-I01 inventories a Windows operator host and checks a proposed APP, DATA, VAULT and optional OBSERVER topology. It produces a redacted planning report. VM creation, Linux boot, application deployment, database operations, tenant admission and physical acceptance have separate procedures.

## Before running

- Use a trusted, clean Git checkout of the source under review. Start a PowerShell 7 session with no profile. The entry point needs Python 3.14 on PATH. If Python is missing, it returns 1 with a fixed diagnostic because it cannot safely parse the configuration or publish evidence.
- The host checks cover Git, GitHub CLI, PowerShell 7, the SDK selected from the checkout by global.json, Node and npm, Python, OpenSSL, OpenSSH and Hyper-V. sqlcmd, LocalDB, WSL, Docker and Compose are optional inventory. The tooling reads installed versions and can flag an installation or an unsupported wrapper.
- The planning target is x64 Windows 10/11 Pro or Enterprise with the full Hyper-V role and management tools. Validate CPU virtualization, SLAT and VM Monitor Mode as separate observations. Home and ARM64 block this topology. WSL2 and a running hypervisor alone provide insufficient evidence. Generation 2 capability is inspected separately from actual guest boot. [Microsoft Hyper-V requirements](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/get-started/install-hyper-v?pivots=windows) · [Generation 2 guest matrix](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/plan/should-i-create-a-generation-1-or-2-virtual-machine-in-hyper-v) · [SQL Server Linux platform support](https://learn.microsoft.com/en-us/sql/linux/install-upgrade/setup?view=sql-server-ver17)
- Use a local fixed NTFS or ReFS volume. Create the configuration, VM storage and evidence parent directories yourself and restrict write access to the operator. Check inherited permissions before collection. Storage and evidence use distinct directories on one drive in V1 so the free-space budget is measured on the correct volume. The script rejects device and shared paths, SUBST drives, traversal, links, junctions, reparse points and Git worktree paths.

## Configure and run

Copy environment.example.json outside Git. Edit the copy to reflect a planned host and topology. Supply opaque tenant and trust references from your own records. For a first run, the example host reference will normally produce HOST_REFERENCE_MISMATCH. Confirm you are on the intended machine, then copy the report's hostRef into operatorHostRef and rerun with a fresh output directory. Keep passwords, tokens, connection strings, private keys, Vault unseal material and approval records outside the plan. The fixed checkout-relative toolchain lock is read from this source. The guest configuration path and TLS reference are planning pointers. The collector does not open the guest configuration or trust material.

V1 models four separate private IPv4 networks for APP, DATA, VAULT and management. Each gateway belongs to its own subnet. Treat the proposed LAB range as the union of these networks. OBSERVER can be planned as a fourth VM on management or remain on Windows by omitting it. Check the example CIDRs against site allocations before reuse.

From the checkout, use a fresh output directory below the configured evidence root. Its parent must already exist.

~~~powershell
Copy-Item ./deploy/lab/windows/environment.example.json 'C:\Lab\Config\environment.json'
# Edit the copy and create the configured storage and evidence roots first.
pwsh -NoProfile -NonInteractive -File ./scripts/lab/Test-LabPrerequisites.ps1 `
  -EnvironmentFile 'C:\Lab\Config\environment.json' `
  -OutputDirectory 'C:\Lab\Evidence\Preflight 01'
$LASTEXITCODE
~~~

The JSON and Markdown reports appear together in the new output directory. Repeat with a fresh directory. A path rejected by the output guard gets a fixed stderr code and no files. Input or probe failures get a redacted failure report when the operator-selected destination can be safely used.

| Exit | Overall status | Interpretation |
| --- | --- | --- |
| 0 | READY_FOR_LAB_SETUP | Required host and planning checks passed |
| 1 | COLLECTION_FAILED | Invalid input, unsafe output, or a required collection step failed |
| 2 | BLOCKED | Collection finished and at least one required observation failed or remains unknown |

Each check records its requirement, observation, expected value where relevant, status and reason code. NOT_TESTED covers checks such as actual guest boot, installer verification and clock drift. NOT_APPLICABLE covers optional tools absent from this host. The report records source HEAD and a clean or dirty checkout state. A dirty or unverified checkout blocks readiness. READY_FOR_LAB_SETUP is a host readiness result. Setup still requires its own approval and independently verified artifacts.

## Resolve blocked checks

1. Correct invalid fields using the example and retry at a fresh output directory. A network conflict identifies the planning role and inventory source, such as APP/WINNAT. Review that source privately on Windows. The report omits literal addresses, gateways, routes, switches and DNS servers. HNS covers the host-visible WSL and container networks when exposed by Windows. Docker queries its known local named pipes. A discovered daemon that cannot be queried leaves coverage unknown. Network coverage is limited to the host and local probes. WSL guest-only networks, custom Docker contexts, split VPNs and disconnected networks need an operator check before provisioning. An unavailable required network source blocks readiness.
2. Review firmware, feature state and pending restart indicators. Missing permission or a masked CIM hardware bit yields UNKNOWN. Resolve permission via the designated host operator. Verify the target Gen 2 Linux image and its boot policy during guest setup.
3. Compare the observed RAM and target volume with the allocation. The 32 GiB example assigns APP 4, DATA 8 and VAULT 2 GiB with 8 GiB of host reserve. For 64 GiB, plan APP 4, DATA 12, VAULT 4 and optional OBSERVER 4 GiB with 16 GiB host reserve. Free memory must cover VM allocation and the reserve. Plan roughly 200–300 GiB of free SSD space. The report requires at least 200 GiB or the sum of VHDX, backup and evidence budgets, whichever is greater. These allocations are estimates. Benchmark the real workloads separately.
4. Diagnose unavailable tools with version-only commands in the trusted checkout. For .NET use dotnet --list-sdks and dotnet --version from the repository root. The repository requires 10.0.404 with latestPatch in the 10.0.4xx band and excludes prereleases. Node uses >=26.13.1 <27. Python 3.14 matches LAB-E05 CI. npm is checked using the npm-cli.js entry point and bundled package metadata shipped with Node. A wrapper without a readable packaged CLI can remain unavailable. Resolve a missing OpenSSL through a reviewed Windows distribution. [Microsoft SDK selection rules](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)
5. Check Windows Time and its configured source. The collector reads the last sync timestamp when recognizable and records clock uncertainty without an independent reference. Local, VM-only or unknown sync sources block the clock prerequisite. The collector performs no resync.

## Verify installation artifacts

toolchain.lock.json records version requirements, proposed versions, official project or release entry points, platform constraints and verification procedures. NOT_PINNED means a verified artifact identity is still pending. Null hashes and identities reflect that state. The version observed on PATH cannot establish installer provenance. The lock and guest software fields remain installation follow-up even if host readiness passes.

1. Select an exact release and platform from the recorded publisher. Obtain its published manifest, signing keys or trusted publisher identity through an independently authenticated channel. For Windows Authenticode artifacts, run Get-AuthenticodeSignature on the exact bytes and check Valid status and the certificate identity against the approved publisher. For Node, verify the signed SHASUMS256 manifest with an independently trusted release key before checking the MSI. [Microsoft Authenticode reference](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/get-authenticodesignature) · [Node versioned releases](https://nodejs.org/download/release/)
2. Compare Get-FileHash -Algorithm SHA256 with the authenticated manifest. If the publisher uses another digest algorithm, verify that published digest and signer first and have the resulting SHA-256 independently reviewed. A hash supplied with an untrusted replacement cannot establish provenance.
3. For OCI images, review the vendor registry and OS/architecture manifest, capture the immutable digest for the selected platform and verify publisher signatures where provided. Record a versioned release plus filename or registry identity and digest after verification. Mutable tags are insufficient. Windows inbox capabilities use Windows publisher and OS servicing provenance. npm inherits the selected Node release verification procedure.
4. If the publisher identity, hash, manifest or signature cannot be verified, keep that artifact blocked for installation. The collector performs no downloads, installations or signature checks. Guest artifacts and the operator's independent verification record are inputs to later increments.

## Evidence handling and LAB-I02 handoff

Keep the report pair in the protected evidence directory. The reports omit raw inventory and input values such as VM and tenant names, paths, private networks and credentials. The redacted host reference is a correlation aid. Inventory probes use local read-only APIs and commands. The collector writes the staged reports and their published directory. Windows APIs and any PowerShell compatibility machinery may use OS-managed temporary state. It makes no changes to Windows features, registry, services, firewall, routes, NAT, switches, WSL or Docker state.

Retain the validated topology and report under operator custody for LAB-I02. Confirm the actual host identity, artifact pins, guest trust and approved target under that increment's procedures. The LAB-I01 plan schema, APP02B-LAB-WINDOWS-PLAN-V1, differs from the LAB-E05 environment schema. E05 separately checks protected original host bytes, externally retained authorization and independent review evidence. LAB-I01 emits no E05 admission manifest and has no role in the isolated source-bound launcher.

GitHub-hosted tests use disposable synthetic inventory and validate source behavior and report publication. A local run can produce HOST_PREFLIGHT_OBSERVED for that machine and source. Physical PREPROD, workloads and admission remain separate. [LAB-E05 evidence contract](../../../docs/roadmap/app-02b-local-preprod/lab-e05-contract.md)
